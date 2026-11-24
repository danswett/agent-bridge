using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Restore;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

public sealed record FleetResumeInput(int Version, Guid Request, string Session);
public sealed record FleetResumeReceipt(Guid Request, string Outcome, string? Reason, string? SessionId = null);
// fleet.resume (#4318, docs/design/agent-resume.md): a coordinator resumes a fleet worker in place. The caller is a live agent
// session at depth 0 with launch scope whose own recorded launch role the person listed in agents.resume.roles; the session is
// one the launch record shows a fleet launch, ended, never the caller's own, resumed with the settings it was launched with.
// Everything else refuses before anything starts, with a closed reason.
public sealed class FleetResumeCommands(string folder, AgentCredentials credentials, Func<string, string?> launchRole,
    Func<string, bool> wasFleetLaunched, Func<string, bool> running, Func<string, RestoreLaunch?> recorded,
    Func<ResumeAsk, CancellationToken, Task<LaunchResult>> resume)
{
    public const string Op = "fleet.resume";
    const int Remembered = 256;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    readonly AgentAuditLog audit = new(folder);
    readonly SemaphoreSlim serial = new(1);
    // A repeat of a request id returns what it first came to and resumes nothing twice; after a host restart the session
    // is simply running, which refuses.
    readonly Dictionary<(string, Guid), FleetResumeReceipt> answered = [];
    readonly Queue<(string, Guid)> order = new();
    public async Task<FleetResumeReceipt> HandleAsync(AgentCredential caller, JsonElement data)
    {
        await serial.WaitAsync();
        try
        {
            var principal = credentials.PrincipalOf(caller);
            FleetResumeInput? input = null;
            FleetResumeReceipt Done(string outcome, string? reason, string? session)
            {
                var receipt = new FleetResumeReceipt(input?.Request ?? Guid.Empty, outcome, reason, session);
                var written = audit.Append(new AgentAuditEntry("local", receipt.Request.ToString(), principal, caller.Chain, Op, session ?? "",
                    outcome, reason ?? "none"));
                if (input is not null)
                {
                    answered[(principal, input.Request)] = receipt;
                    order.Enqueue((principal, input.Request));
                    while (order.Count > Remembered) { answered.Remove(order.Dequeue()); }
                }
                return written || outcome == "refused" ? receipt : receipt with { Outcome = "unconfirmed", Reason = "audit-unavailable" };
            }
            FleetResumeReceipt Refused(string reason, string? session = null) => Done("refused", reason, session);
            try
            {
                if (data.ValueKind != JsonValueKind.Object || ProtocolJson.ByteCount(data) > 4096
                    || data.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)) { return Refused("bad-argument"); }
                input = data.Deserialize<FleetResumeInput>(Json);
                if (input is null || input.Version != 1 || input.Request == Guid.Empty || !FleetSessionId.IsCanonical(input.Session))
                { input = null; return Refused("bad-argument"); }
                if (answered.TryGetValue((principal, input.Request), out var first)) { return first; }
                if (!credentials.IsLive(caller) || caller.Kind != "session" || caller.Scope != AgentProtocol.ScopeReadReplyLaunch
                    || caller.Depth != 0 || caller.LaunchId == AgentCredentials.AdoptedLaunch) { return Refused("policy"); }
                var (settings, problem) = HostSettings.Load(folder);
                if (problem is not null || settings.SectionProblems.Any()) { return Refused("policy"); }
                if (settings.ReadOnly || !settings.Agents.Enabled || !settings.Launch.Enabled) { return Refused("kill-switch"); }
                // A coordinator by the role its own launch recorded, listed by the person; never by name, and none listed means off.
                string? role;
                try { role = launchRole(caller.Session); } catch (InvalidOperationException) { role = null; }
                if (role is null || !settings.Agents.ResumeRoles.Contains(role, StringComparer.Ordinal)) { return Refused("policy"); }
                var session = input.Session;
                if (string.Equals(session, caller.Session, StringComparison.OrdinalIgnoreCase)
                    || settings.Agents.ExcludedSessions.Contains(session)) { return Refused("scope", session); }
                bool fleet;
                try { fleet = wasFleetLaunched(session); } catch (InvalidOperationException) { fleet = false; }
                if (!fleet) { return Refused("scope", session); }
                if (running(session)) { return Refused("running", session); }
                // The original settings or nothing: never the caller's, and never the machine's defaults.
                if (recorded(session) is not { } launch) { return Refused("policy", session); }
                var result = await resume(new ResumeAsk(session, Enum.TryParse<AgentKind>(launch.Launcher, true, out var kind) ? kind : AgentKind.Copilot, launch, principal),
                    CancellationToken.None);
                return result.Outcome switch
                {
                    LaunchOutcome.Started => Done("resumed", null, session),
                    LaunchOutcome.Busy => Refused("busy", session),
                    LaunchOutcome.Refused => Refused("policy", session),
                    _ => Done("unconfirmed", "failed", session),
                };
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            { return input is null ? Refused("bad-argument") : Done("unconfirmed", "audit-unavailable", input.Session); }
        }
        finally { serial.Release(); }
    }
}
