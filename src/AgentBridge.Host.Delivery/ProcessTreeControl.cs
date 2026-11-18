namespace AgentBridge.Host.Delivery;

internal sealed class ProcessTreeControl(Func<int, DateTimeOffset, HeldOpen> open,
    ProcessTreeEvidence evidence, TimeProvider? time = null) : IProcessControl
{
    internal ProcessTreeEvidence Evidence { get; } = evidence;
    internal TimeProvider Time { get; } = time ?? TimeProvider.System;
    public HeldOpen Open(int processId, DateTimeOffset startedAt)
    {
        var result = open(processId, startedAt);
        if (result.Process is not { } root) { return result; }
        var tree = new Tree(this, root);
        var keep = false;
        try
        {
            tree.Collect();
            if (tree.Refusal is { } why) { return HeldOpen.Refused(why); }
            keep = true;
            return HeldOpen.Of(tree);
        }
        finally { if (!keep) { tree.Dispose(); } }
    }

    enum Failure { None, Snapshot, Identity, Limit }

    sealed class Tree(ProcessTreeControl owner, IHeldProcess root) : IHeldProcess
    {
        static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);
        readonly List<IHeldProcess> held = [root];
        readonly List<IHeldProcess> excluded = [];
        readonly HashSet<int> seen = [root.Id];
        Failure failure;
        string? problem, terminationError, waitProblem;
        bool lostAncestry, terminating;
        int dispatched;
        public int Id => root.Id;
        public DateTimeOffset? StartedAt => root.StartedAt;
        public string? ExitProblem => problem ?? waitProblem ?? terminationError;
        public string? Refusal => failure == Failure.Identity ? problem : null;

        bool Unknown(Failure kind, string why) { failure = kind; problem = why; return false; }

        public bool Collect(TimeSpan? remaining = null)
        {
            if (lostAncestry || failure is Failure.Identity or Failure.Limit) { return false; }
            failure = Failure.None;
            problem = null;
            var began = owner.Time.GetTimestamp();
            bool Allowed()
            {
                var elapsed = owner.Time.GetElapsedTime(began);
                return elapsed < Budget && (remaining is null || elapsed <= remaining);
            }
            bool Limit() => Unknown(Failure.Limit, "the process tree exceeded its handle, descendant or collection-time limit");
            if (owner.Evidence.Capture(Allowed) is not { } map)
            {
                return Allowed() ? Unknown(Failure.Snapshot, "the host could not read a complete process tree") : Limit();
            }
            var candidates = 0;
            for (var i = 0; i < held.Count; i++)
            {
                foreach (var id in map.Children(held[i].Id))
                {
                    if (seen.Contains(id)) { continue; }
                    if (!Allowed() || held.Count + excluded.Count >= 64 || ++candidates > 64) { return Limit(); }
                    var proof = owner.Evidence.Child(held[i], id, map, Allowed);
                    if (!Allowed()) { proof.Process?.Dispose(); return Limit(); }
                    if (proof.Process is not { } child)
                    {
                        // Losing a discovered identity cannot later be healed by an empty snapshot.
                        lostAncestry = true;
                        return Unknown(proof.TreeUnknown ? Failure.Snapshot : Failure.Identity,
                            proof.Problem ?? "a descendant's identity could not be verified");
                    }
                    seen.Add(id);
                    // Even excluded older identities stay pinned against PID reuse until exit proof.
                    if (proof.Unrelated) { excluded.Add(child); } else { held.Add(child); }
                }
            }
            if (!Allowed()) { return Limit(); }
            return true;
        }

        void Dispatch()
        {
            for (; dispatched < held.Count; dispatched++)
            {
                var error = held[dispatched].Terminate();
                terminationError ??= error;
            }
        }

        public string? Terminate()
        {
            if (terminating) { return terminationError ?? problem; }
            terminating = true;
            if (Collect()) { Dispatch(); }
            else
            {
                // Incomplete tree proof must not strand the root already verified by handle.
                terminationError = root.Terminate();
                dispatched = 1;
            }
            return terminationError ?? problem;
        }

        public async Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            waitProblem = null;
            var began = owner.Time.GetTimestamp();
            // One initial collection and at most two post-wait rescans share this deadline.
            for (var pass = 0; pass < 2; pass++)
            {
                if (pass == 0 && !Collect(timeout - owner.Time.GetElapsedTime(began)))
                {
                    if (terminating)
                    {
                        var remaining = timeout - owner.Time.GetElapsedTime(began);
                        await root.WaitForExitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                    }
                    return false;
                }
                if (terminating) { Dispatch(); }
                foreach (var process in held)
                {
                    var remaining = timeout - owner.Time.GetElapsedTime(began);
                    if (!await process.WaitForExitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
                    { waitProblem = "a verified process is still running"; return false; }
                }
                var count = held.Count;
                if (!Collect(timeout - owner.Time.GetElapsedTime(began))) { return false; }
                if (held.Count == count && owner.Time.GetElapsedTime(began) <= timeout) { return true; }
            }
            waitProblem = "the process-tree exit deadline or rescan limit was reached";
            return false;
        }

        public void Dispose()
        {
            foreach (var process in excluded) { process.Dispose(); }
            for (var i = held.Count - 1; i >= 0; i--) { held[i].Dispose(); }
        }
    }
}
