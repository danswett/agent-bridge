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
        Question = Question(s.Question),
        PermissionPending = s.Question?.Kind == QuestionKind.Approval,
    };

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
}

// The running host for a profile, through its own pipe (docs/protocol/local-pipe.md): what this
// host actually shows the user's devices. A connection each sample, so a host that restarted is
// simply found again.
public sealed class PipeShadowHost(LocalEndpoint endpoint) : IShadowHost
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public async Task<ShadowView> ObserveAsync(CancellationToken ct = default)
    {
        try
        {
            await using var channel = await LocalClient.ConnectAsync(endpoint, TimeSpan.FromSeconds(3), ct);
            await channel.HelloAsync(ct);
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
    });
}
