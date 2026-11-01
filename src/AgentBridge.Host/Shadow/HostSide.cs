using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Shadow;

// The host's side of the comparison. Only reads: hello, host.status and sessions.list on the pipe,
// or a scan in this process. Never an answer, a reply, a stop or a launch.
public interface IShadowHost
{
    Task<ShadowView> ObserveAsync(CancellationToken ct = default);
}

public static class HostSide
{
    public static ShadowSession From(Session s) => new()
    {
        Id = s.Id,
        Agent = s.Agent.ToString().ToLowerInvariant(),
        Status = ShadowCompare.HostStatus(s.Status),
        Agents = s.Activity.Background?.Agents ?? 0,
        Shells = s.Activity.Background?.Shells ?? 0,
        Model = string.IsNullOrEmpty(s.Model) ? null : s.Model,
        Compactions = s.Activity.Compactions ?? 0,
        ResponseTail = ShadowHash.Tail(s.Activity.Response),
        CardResponseTail = s.Activity.Source?.Response is null ? null : ShadowHash.Tail(HaPayloads.MainResponse(s.Activity)) ?? ShadowCompare.NoResponse,
        RunningSubagents = RunningSubagents(s),
        Question = Question(s.Question),
        PermissionPending = s.Question?.Kind == QuestionKind.Approval,
    };

    // What background.agents counts, entry by entry: background sub-agents working or waiting, at
    // any depth (subagents.md §2). Running ones are listed first, so the list holds them all unless
    // nothing finished follows them and some were left out.
    static IReadOnlyList<string>? RunningSubagents(Session s)
    {
        var list = s.Subagents ?? [];
        if (s.SubagentsMore is > 0 && !list.Any(a => a.Finished)) { return null; }
        return [.. list.Where(a => a.Kind == SubagentKind.Background && !a.Finished).Select(a => a.Id)];
    }

    // Permission prompts are not compared: agent-ha-bridge only mirrors them as an alert and arms
    // no question for them, where this host offers them to approve (parity §3, beyond parity).
    static ShadowQuestion? Question(Question? q) => q is null || q.Kind == QuestionKind.Approval ? null : new()
    {
        CallId = q.Id,
        AskedAt = q.AskedAt,
        MessageHead = ShadowHash.Head(q.Message),
        // A question cut to fit the budget may have lost its fields, and one no device can answer
        // is shaped for the machine; agent-ha-bridge sends both to its terminal too.
        AtMachine = q.AnswerAtMachine == true || q.Truncated == true,
        Fields = ShadowCompare.Canonical([.. q.Fields.Select(Field)]),
    };

    // In agent-ha-bridge's terms: a number is typed like text, and a checkbox is a choice of
    // true or false (Get-DecisionSchemaFieldChoices).
    static ShadowField Field(Field f) => f.Type switch
    {
        FieldType.Text or FieldType.Number => new("text", 0, null),
        FieldType.Boolean => new("choice", 2, ShadowHash.Options(["true", "false"])),
        _ => new(f.Type == FieldType.Multi ? "multi" : "choice", f.Options?.Count ?? 0, ShadowHash.Options((f.Options ?? []).Select(o => o.Value))),
    };

    public static IReadOnlyDictionary<string, ShadowSession> Map(IEnumerable<Session> sessions)
    {
        var map = new Dictionary<string, ShadowSession>(StringComparer.Ordinal);
        foreach (var s in sessions) { map[s.Id] = From(s); }
        return map;
    }

    // Under the names agent-ha-bridge gives its usage sensors (HaPayloads.Usage), the first
    // reading of each agent, as the host's Home Assistant connector publishes it, and each further
    // Copilot login's under its numbered sensor (copilot_2 for the first listed).
    public static IReadOnlyDictionary<string, ShadowUsage> Usage(UsageReport report)
    {
        var map = new Dictionary<string, ShadowUsage>(StringComparer.Ordinal);
        foreach (var a in report.Agents)
        {
            var client = a.Agent switch { AgentKind.Copilot => "copilot", AgentKind.Claude => "claude", AgentKind.Codex => "codex", _ => null };
            if (client is not null && a.Slot is { } slot) { client = $"{client}_{slot}"; }
            if (client is null || map.ContainsKey(client)) { continue; }
            var windows = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var w in a.Windows) { windows.TryAdd(w.Key, w.Percent); }
            map[client] = new ShadowUsage
            {
                Reading = windows.Count > 0,
                Account = a.Account,
                AccountKey = a.AccountKey,
                Windows = windows,
                MeasuredAt = a.MeasuredAt,
            };
        }
        return map;
    }

    // A host that approves no folder offers nothing to resume, by design: that is its config,
    // not a disagreement about the sessions (docs/host-shadow.md says to approve the same ones).
    public static (IReadOnlyList<string>?, string?) Resumable(LaunchOptions options) =>
        options.Workspaces.Count == 0
            ? (null, "the host approves no workspace, so offers nothing to resume")
            : ([.. options.Resumable.Select(r => ShadowHash.Normalize(r.Title))], null);
}

// The running host for a profile, through its own pipe (docs/protocol/local-pipe.md): what this
// host actually shows the user's devices. A connection each sample, so a host that restarted is
// simply found again. Usage and the resume list are asked for at most once a minute: listing
// resumable sessions reads every agent's store (and asks Agency), and neither side's figure
// moves faster than that.
public sealed class PipeShadowHost(LocalEndpoint endpoint, TimeProvider? time = null, TimeSpan? refresh = null) : IShadowHost
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly TimeSpan every = refresh ?? TimeSpan.FromMinutes(1);
    DateTimeOffset? asked;
    (IReadOnlyDictionary<string, ShadowUsage>? Usage, string? UsageProblem, IReadOnlyList<string>? Resumable, string? ResumableProblem) extras;

    public async Task<ShadowView> ObserveAsync(CancellationToken ct = default)
    {
        try
        {
            await using var channel = await LocalClient.ConnectAsync(endpoint, TimeSpan.FromSeconds(3), ct);
            var hello = await channel.HelloAsync(ct);
            // Usage and the resume list first: launch.options can take up to a minute, and the
            // sessions read after it would be that much older than the daemon's files read
            // next, so a busy session's activity would differ by however long it took.
            var now = clock.GetUtcNow();
            if (asked is not { } at || now - at >= every)
            {
                extras = await ExtrasAsync(channel, hello.Capabilities.Contains(UsageProtocol.Capability), ct);
                asked = now;
            }
            var status = await channel.CallAsync<HostStatus>(HostOps.Status, timeout: Patience, ct: ct);
            var sessions = new List<Session>();
            string? cursor = null;
            for (var page = 0; page < 1000; page++)
            {
                var list = await channel.CallAsync<SessionList>(Ops.SessionsList, new ListRequest(cursor), Patience, ct);
                sessions.AddRange(list.Sessions);
                cursor = list.Next;
                if (cursor is null) { break; }
            }
            return new()
            {
                Sessions = HostSide.Map(sessions),
                Version = ShadowHash.Token(status.Version),
                Dev = status.Dev,
                SeesAgentHaBridge = status.AgentHaBridge is not null,
                StartedAt = status.StartedAt,
                Usage = extras.Usage,
                UsageProblem = extras.UsageProblem,
                Resumable = extras.Resumable,
                ResumableProblem = extras.ResumableProblem,
            };
        }
        catch (HostNotRunningException) { return new() { Problem = "the host is not running for this profile" }; }
        catch (LocalRefusedException) { return new() { Problem = "the host refused this process" }; }
        // Whatever else a connection does (a closed pipe, a timeout, a message that is not what
        // was asked for) is this sample's host not answering; the comparison goes on.
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new() { Problem = "the host did not answer" };
        }
    }

    // Each asked on its own, so a host that does not answer one still has the other compared.
    static async Task<(IReadOnlyDictionary<string, ShadowUsage>?, string?, IReadOnlyList<string>?, string?)> ExtrasAsync(LocalChannel channel, bool offersUsage, CancellationToken ct)
    {
        IReadOnlyDictionary<string, ShadowUsage>? usage = null;
        string? usageProblem = null;
        if (!offersUsage) { usageProblem = "the host does not offer usage"; }
        else
        {
            try { usage = HostSide.Usage(await channel.CallAsync<UsageReport>(UsageProtocol.Get, timeout: Patience, ct: ct)); }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { usageProblem = "the host did not answer usage.get"; }
        }
        IReadOnlyList<string>? resumable = null;
        string? resumableProblem = null;
        try
        {
            // Listing resumable sessions asks Agency, which a cold machine can take a while to answer.
            var options = await channel.CallAsync<LaunchOptions>(LaunchProtocol.Options, timeout: TimeSpan.FromSeconds(60), ct: ct);
            (resumable, resumableProblem) = HostSide.Resumable(options);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { resumableProblem = "the host did not answer launch.options"; }
        return (usage, usageProblem, resumable, resumableProblem);
    }
}

// A scan in this process, for when no host is serving the profile: the same read-only core the
// host runs, with the same agent-ha-bridge probe it asks before delivering anything.
public sealed class SourceShadowHost(ISessionSource source, IAgentHaBridgeProbe? probe, string version, bool dev) : IShadowHost
{
    public Task<ShadowView> ObserveAsync(CancellationToken ct = default) => Task.FromResult(new ShadowView
    {
        Sessions = HostSide.Map(source.Snapshot()),
        Version = ShadowHash.Token(version),
        Dev = dev,
        SeesAgentHaBridge = probe is null ? null : probe.Active() is not null,
        UsageProblem = "the host's is read only through its pipe (--in-process)",
        ResumableProblem = "the host's is read only through its pipe (--in-process)",
    });
}
