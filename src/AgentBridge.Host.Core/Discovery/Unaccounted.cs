using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Discovery;

// Agent processes no session accounts for, and which of them still hold discovery open:
// agent-ha-bridge's Select-DaemonHoldingProcesses (hooks/daemon-discovery.ps1).
//
// A process discovery cannot match may be a session part-way through starting (no lock or
// registration yet), and reading it as absent is what deletes things. So it holds. But a
// program that embeds or renames an agent binary and never registers held agent-ha-bridge open
// for as long as it ran, twice in one day, refusing every launch (#144). A session registers
// within seconds, so one still unaccounted for after Grace is positive evidence it is not a
// session, and is carried on without, with one log line naming it.
internal sealed class Unaccounted(TimeProvider time, Action<string> log)
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    // How long a record outlives the last pass that saw it. Forgetting on the first pass that
    // missed a process made the grace unreachable for one that flickers in and out (an
    // intermittently unreadable command line): its clock restarted each time, and on a Mac it
    // held 207 times in six hours without once being excused. The key carries the start time,
    // so seeing it again is the same process; retention only bounds the table.
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);

    sealed class Record(DateTimeOffset since)
    {
        public DateTimeOffset Since { get; } = since;
        public DateTimeOffset LastSeen { get; set; } = since;
        public bool Reported { get; set; }
    }

    // Keyed by agent, pid and start time, so a reused pid starts its own clock rather than
    // inheriting the silence of whatever held that number before.
    readonly Dictionary<(AgentKind Agent, int Pid, long Started), Record> records = [];

    // Of this pass's unaccounted processes, those that still hold. Present is every process of
    // this agent the pass looked at, so "accounted for now" can be told from "not here".
    public IReadOnlyList<AgentProcess> Holding(AgentKind agent, IReadOnlyList<AgentProcess> unaccounted, IReadOnlyList<AgentProcess> present)
    {
        var now = time.GetUtcNow();
        var name = agent.ToString().ToLowerInvariant();
        var seen = new HashSet<(AgentKind, int, long)>();
        var holding = new List<AgentProcess>();
        foreach (var p in unaccounted)
        {
            var key = (agent, p.Id, p.StartedAt?.UtcTicks ?? 0);
            seen.Add(key);
            if (!records.TryGetValue(key, out var record)) { records[key] = record = new Record(now); }
            record.LastSeen = now;
            // Without a start time nothing tells this process from a new session given its pid
            // after it exits, which would inherit the elapsed clock and be excused on sight while
            // still registering: the one outcome the grace exists to prevent.
            if (p.StartedAt is null)
            {
                holding.Add(p);
                if (!record.Reported)
                {
                    record.Reported = true;
                    log($"holding for {name} process {p.Id}: it has no readable start time, so it cannot be told apart from a new session reusing its pid");
                }
                continue;
            }
            var waited = now - record.Since;
            if (waited < Grace) { holding.Add(p); continue; }
            if (!record.Reported)
            {
                record.Reported = true;
                log($"carrying on without {name} process {p.Id}: unaccounted for {(int)waited.TotalMinutes} min, so it is not a session that is still starting up");
            }
        }

        var accounted = present.Select(p => (agent, p.Id, p.StartedAt?.UtcTicks ?? 0)).ToHashSet();
        foreach (var (key, record) in records.Where(r => r.Key.Agent == agent && !seen.Contains(r.Key)).ToList())
        {
            // Accounted for now: it registered. Its clock goes at once, so should that
            // registration be lost later it earns a fresh grace rather than being excused on
            // sight while it registers again (found in review of agent-ha-bridge #140).
            if (accounted.Contains(key) || now - record.LastSeen >= Retention) { records.Remove(key); }
        }
        return holding;
    }
}
