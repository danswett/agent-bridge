using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.Rehearsal;

// The running host, asked over its local pipe as the Windows app asks it: what it offers in
// hello, what host.status says, and the sessions it lists.
internal static class HostChecks
{
    public static async Task<Outcome> HostAsync(string profileFolder, Parsed p, CancellationToken ct)
    {
        await using var channel = await ConnectAsync(profileFolder, ct);
        var hello = await channel.HelloAsync(ct);
        var status = await channel.CallAsync<HostStatus>(HostOps.Status, ct: ct);
        var caps = hello.Capabilities.ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        problems.AddRange(p.All("--expect-cap").Where(c => !caps.Contains(c)).Select(c => $"hello does not offer {c}"));
        problems.AddRange(p.All("--expect-no-cap").Where(caps.Contains).Select(c => $"hello offers {c}"));
        if (p.Flag("--expect-delivering") && status.AgentHaBridge is { } why) { problems.Add($"the host is not delivering: {why}"); }
        if (p.Flag("--expect-read-only") && status.AgentHaBridge is null) { problems.Add("the host says agent-ha-bridge is not running, so it is delivering"); }
        var data = new
        {
            machine = hello.Machine.Name,
            version = status.Version,
            dev = status.Dev,
            pid = status.ProcessId,
            elevated = status.Elevated,
            relay = status.Relay.State.ToString(),
            agentHaBridge = status.AgentHaBridge,
            capabilities = hello.Capabilities,
            log = status.Log,
        };
        if (p.Flag("--try-restart"))
        {
            // Invariant 13 (#44): run from inside a session, this must be refused. If the host
            // takes it, it restarts, which is the failure being looked for.
            try
            {
                await channel.CallAsync(HostOps.Restart, ct: ct);
                problems.Add("the host accepted host.restart from this process");
            }
            catch (HostErrorException e) { return problems.Count == 0 ? Outcome.Pass($"host.restart refused: {e.Message}", data) : Outcome.Fail(string.Join("; ", problems), data); }
            catch (LocalRefusedException e) { return problems.Count == 0 ? Outcome.Pass($"the pipe refused this process: {e.Message}", data) : Outcome.Fail(string.Join("; ", problems), data); }
        }
        return problems.Count == 0 ? Outcome.Pass($"host {status.Version}{(status.Dev ? " (dev)" : "")}, pid {status.ProcessId}, {(status.AgentHaBridge is null ? "delivering" : "read-only")}", data)
            : Outcome.Fail(string.Join("; ", problems), data);
    }

    public static async Task<Outcome> SessionsAsync(string profileFolder, Parsed p, CancellationToken ct)
    {
        await using var channel = await ConnectAsync(profileFolder, ct);
        await channel.HelloAsync(ct);
        // With --timeout, asked again each second until the expectation holds: a status
        // a step causes takes a moment to reach the transcript and the host.
        var until = p.Option("--timeout") is null ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow + p.Timeout(0);
        while (true)
        {
            var all = new List<Session>();
            string? cursor = null;
            do
            {
                var page = await channel.CallAsync<SessionList>(Ops.SessionsList, new ListRequest(cursor), ct: ct);
                all.AddRange(page.Sessions);
                cursor = page.Next;
            }
            while (cursor is not null);
            var outcome = Sessions(all, p);
            if (outcome.Verdict == Verdict.Pass || DateTimeOffset.UtcNow >= until) { return outcome; }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    internal static Outcome Sessions(IReadOnlyList<Session> all, Parsed p)
    {
        var id = p.Option("--id");
        var agent = p.Option("--agent");
        var title = p.Option("--title");
        var status = p.Option("--status");
        var model = p.Option("--model");
        var picked = all.Where(s =>
            (id is null || s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) &&
            (agent is null || s.Agent.ToString().Equals(agent, StringComparison.OrdinalIgnoreCase)) &&
            (title is null || (s.Title ?? "").Contains(title, StringComparison.OrdinalIgnoreCase)) &&
            (model is null || (s.Model ?? "").Contains(model, StringComparison.OrdinalIgnoreCase)) &&
            (status is null || s.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase))).ToList();
        var data = picked.Select(s => new
        {
            id = s.Id,
            agent = s.Agent.ToString().ToLowerInvariant(),
            status = s.Status.ToString().ToLowerInvariant(),
            title = s.Title,
            model = s.Model,
            pid = s.ProcessId,
            compactions = s.Activity.Compactions,
            background = s.Activity.Background is { } b ? new { b.Agents, b.Shells } : null,
            question = s.Question is { } q ? new { q.Id, kind = q.Kind.ToString().ToLowerInvariant(), fields = q.Fields.Count, answerAtMachine = q.AnswerAtMachine == true, truncated = q.Truncated == true } : null,
        }).ToList();
        var problems = new List<string>();
        if (p.Int("--expect-count") is { } n && picked.Count != n) { problems.Add($"{picked.Count} session(s) match, expected {n}"); }
        if (p.Int("--expect-min") is { } min && picked.Count < min) { problems.Add($"{picked.Count} session(s) match, expected at least {min}"); }
        if (p.Flag("--expect-gone") && picked.Count > 0) { problems.Add("the session is still listed"); }
        if (p.Flag("--expect-question") && !picked.Any(s => s.Question is { AnswerAtMachine: not true })) { problems.Add("no matching session has a question that can be answered from here"); }
        if (p.Flag("--expect-answer-at-machine") && !picked.Any(s => s.Question is { AnswerAtMachine: true })) { problems.Add("no matching session has a question marked for the machine"); }
        if (p.Flag("--expect-background") && !picked.Any(s => s.Activity.Background is not null)) { problems.Add("no matching session is waiting on background agents or shells"); }
        if (p.Int("--expect-compactions") is { } c && !picked.Any(s => (s.Activity.Compactions ?? 0) == c)) { problems.Add($"no matching session has {c} compaction(s)"); }
        if (picked.Count == 0 && !p.Flag("--expect-gone") && p.Int("--expect-count") is not 0) { problems.Add("no session matches"); }
        if (p.Flag("--id-only"))
        {
            if (picked.Count != 1) { problems.Add($"--id-only needs exactly one matching session, and {picked.Count} match"); }
            return problems.Count == 0 ? Outcome.Pass(picked[0].Id) : Outcome.Fail(string.Join("; ", problems), data);
        }
        var summary = string.Join(", ", picked.Take(5).Select(s => $"{s.Id[..Math.Min(8, s.Id.Length)]} {s.Agent.ToString().ToLowerInvariant()} {s.Status.ToString().ToLowerInvariant()}"));
        return problems.Count == 0 ? Outcome.Pass($"{picked.Count} session(s){(summary.Length > 0 ? ": " + summary : "")}", data) : Outcome.Fail(string.Join("; ", problems), data);
    }

    static async Task<LocalChannel> ConnectAsync(string profileFolder, CancellationToken ct) =>
        await LocalClient.ConnectAsync(LocalEndpoint.For(profileFolder), TimeSpan.FromSeconds(5), ct);

    public static async Task<string?> LogPathAsync(string profileFolder, CancellationToken ct)
    {
        var fallback = Path.Combine(profileFolder, "logs", "host.log");
        try
        {
            await using var channel = await ConnectAsync(profileFolder, ct);
            await channel.HelloAsync(ct);
            var status = await channel.CallAsync<HostStatus>(HostOps.Status, ct: ct);
            return status.Log ?? fallback;
        }
        catch (Exception e) when (e is IOException or HostErrorException or JsonException) { return File.Exists(fallback) ? fallback : null; }
    }
}
