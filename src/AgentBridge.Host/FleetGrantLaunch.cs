using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public sealed record FleetLaunchInput(int Version, Guid Request, Guid Grant, string Name, string Workspace, string Agent,
    string? Target = null, string? Model = null, string? LaunchProfile = null, string? Effort = null, string? Context = null,
    string? Permissions = null, bool Isolated = false, string? Prompt = null);
public sealed record FleetLaunchReceipt(Guid Request, Guid Grant, long Generation, LaunchResult Result, string? Permissions,
    bool Registered, GrantUsage? Usage, GrantCaps? Caps, string? Reason = null);
internal sealed record FleetLaunchContext(FleetLaunchInput Input, GrantSubject Subject, string Binding,
    GrantDocument? Grant = null, LaunchRequest? Request = null, LaunchOptions? Offered = null, FleetLaunchReceipt? Replay = null,
    GrantCaps? Caps = null)
{
    public bool Admitted, Starting;
}
internal sealed class FleetLaunchDenied(string reason) : InvalidOperationException(reason);

public sealed class FleetGrantLaunch(string folder, P256KeyPair key, Func<string?> account, string device,
    AgentCredentials credentials, SessionService sessions, ISessionLauncher launcher)
{
    readonly GrantStore store = new(folder, key, new(folder), new([], [], [], [], [], [], [], []));
    readonly GrantCounters counters = new(new(folder, key, new(folder), new([], [], [], [], [], [], [], [])));
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };

    internal static FleetLaunchInput Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || ProtocolJson.ByteCount(data) > LaunchProtocol.MaxRequest
            || data.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)) { throw new FleetLaunchDenied("bad-argument"); }
        var input = data.Deserialize<FleetLaunchInput>(Json);
        if (input is null || input.Version != 1 || input.Request == Guid.Empty || input.Grant == Guid.Empty
            || string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Workspace)
            || !LaunchProtocol.Agents.Contains(input.Agent, StringComparer.Ordinal) || input.Permissions is not (null or "ask" or "allowAll")
            || new[] { input.Model, input.Effort, input.Context }.Any(v => v is not null && !LaunchProtocol.IsTuning(v))
            || input.LaunchProfile is not null && !Identity.IsValidProfile(input.LaunchProfile))
        { throw new FleetLaunchDenied("bad-argument"); }
        return input;
    }
    internal async Task<FleetLaunchContext> PrepareAsync(AgentCredential caller, FleetLaunchInput input)
    {
        if (!credentials.IsLive(caller) || caller.Kind != "session" || caller.Scope != AgentProtocol.ScopeReadReplyLaunch
            || caller.Depth != 0 || caller.LaunchId == AgentCredentials.AdoptedLaunch) { throw new FleetLaunchDenied("policy"); }
        var subject = new GrantSubject(account() ?? "", device, caller.Session, caller.Id, new(caller.RootPid, caller.RootStartUtc));
        var binding = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, ProtocolJson.Options)));
        var context = new FleetLaunchContext(input, subject, binding);
        var old = counters.Lookup(input.Grant, input.Request, subject, binding);
        if (old.Problem == GrantCounterProblem.Duplicate) { return context with { Replay = Recorded(context, old) }; }
        if (old.Problem == GrantCounterProblem.Binding) { throw new FleetLaunchDenied("duplicate-id"); }
        if (old.Problem != GrantCounterProblem.NotFound) { throw new FleetLaunchDenied(CounterReason(old)); }
        var found = store.TryGetUsable(input.Grant);
        if (!found.Usable) { throw new FleetLaunchDenied(StoreReason(found.Problem)); }
        context = context with { Grant = found.Grant, Offered = await launcher.OptionsAsync() };
        var effective = Evaluate(context, caller);
        var reconciled = counters.Reconcile(input.Grant, row =>
        {
            var known = launcher.LaunchStatuses().SingleOrDefault(s => Guid.TryParse(s.Request, out var request) && request == row.Request);
            return new(known?.Permissions is "ask" or "allowAll" && known.Session is { } id ? sessions.GrantTarget(id) : null, Permissions: known?.Permissions);
        }, Observe, ended: session => credentials.LaunchGate.SessionEnded(session));
        if (!reconciled.Accepted) { throw new FleetLaunchDenied(CounterReason(reconciled)); }
        return context with { Caps = effective.Caps, Request = new LaunchRequest { Request = input.Request.ToString("D"), Agent = input.Agent,
            Workspace = input.Workspace, FleetName = input.Name, Model = effective.Model, Effort = effective.Effort,
            Context = effective.Context, Profile = input.Agent == "agency" ? effective.Profile : null,
            Permissions = effective.Permission, Prompt = input.Prompt } };
    }
    EffectiveLaunch Evaluate(FleetLaunchContext context, AgentCredential caller)
    {
        var input = context.Input;
        var (settings, problem) = HostSettings.Load(folder);
        if (problem is not null || settings.SectionProblems.Any()) { throw new FleetLaunchDenied("policy"); }
        if (settings.ReadOnly || !settings.Launch.Enabled || !settings.Agents.Enabled || !settings.Agents.LaunchEnabled)
        { throw new FleetLaunchDenied("kill-switch"); }
        var inbound = settings.Agents.InboundLocal.Launch;
        var agent = context.Offered!.Agents.SingleOrDefault(a => a.Id == input.Agent);
        if (inbound is null || agent is null || !inbound.Agents.Contains(input.Agent)
            || settings.Agents.ExcludedSessions.Contains(caller.Session) || settings.Agents.ExcludedSessions.Contains(input.Name)
            || settings.Agents.ExcludedWorkspaces.Contains(input.Workspace)) { throw new FleetLaunchDenied("scope"); }
        if (input.Target is not null && input.Target != device && input.Target != "agent_bridge_" + device[..16])
        { throw new FleetLaunchDenied("target-offline"); }
        var workspaces = context.Offered.Workspaces.Where(w => w.Isolate == true
            && (inbound.Workspaces.Contains("approved") || inbound.Workspaces.Contains(w.Label))).Select(w => w.Label).ToImmutableArray();
        var scope = new GrantScope([device], workspaces, [input.Agent], [.. agent.Models],
            input.Agent == "agency" ? [.. (agent.Profiles ?? []).Where(inbound.Profiles.Contains)] : ["default"],
            [.. agent.Efforts], [.. agent.Contexts], inbound.AllowAllLaunch ? ["ask", "allowAll"] : ["ask"]);
        var limits = settings.Agents.Limits.Launch;
        var policy = new HostPolicySnapshot(account() ?? "", new(device, Path.GetFileName(folder)), context.Subject, scope,
            new(Math.Min(limits.LaunchesPerOriginPerHour, limits.LaunchesPerMachinePerHour), 5000, limits.LiveAgentLaunched), true, false, inbound.AllowAllLaunch);
        var decision = GrantEvaluator.Evaluate(context.Grant!, policy, new(context.Subject, "launch", device, input.Workspace,
            input.Agent, input.Model, input.LaunchProfile, input.Effort, input.Context, input.Permissions), DateTimeOffset.UtcNow);
        return decision.Launch is { } launch ? launch : throw new FleetLaunchDenied("scope");
    }
    internal string? Check(FleetLaunchContext context, AgentCredential caller)
    {
        if (!credentials.IsLive(caller)) { return "policy"; }
        var latest = store.TryGetUsable(context.Input.Grant, context.Grant!.Generation);
        if (!latest.Usable) { return StoreReason(latest.Problem); }
        try
        {
            var effective = Evaluate(context with { Grant = latest.Grant }, caller);
            var recorded = counters.Lookup(context.Input.Grant, context.Input.Request, context.Subject, context.Binding);
            if (recorded.Reservation?.Caps is not { } snapshot || recorded.Usage is not { } usage) { return "policy"; }
            var caps = snapshot.Intersect(effective.Caps);
            if (usage.Hourly > caps.Hourly || usage.Daily > caps.Daily || usage.Live > caps.Live) { return "grant-exhausted"; }
            if (effective.Permission != context.Request!.Permissions || effective.Model != context.Request.Model
                || effective.Effort != context.Request.Effort || effective.Context != context.Request.Context
                || context.Input.Agent == "agency" && effective.Profile != context.Request.Profile) { return "scope"; }
            return launcher.FleetConfig is { } running ? credentials.FleetGrant.CheckDispatch(running, context.Request) : "policy";
        }
        catch (FleetLaunchDenied e) { return e.Message; }
    }
    internal void Admit(FleetLaunchContext context)
    {
        var result = counters.Reserve(context.Input.Grant, context.Grant!.Generation, context.Input.Request,
            context.Subject, context.Binding, context.Caps ?? throw new FleetLaunchDenied("policy"));
        if (!result.Accepted) { throw new FleetLaunchDenied(CounterReason(result)); }
        context.Admitted = true;
    }
    internal FleetLaunchReceipt Finish(FleetLaunchContext context, LaunchResult result)
    {
        if (context.Admitted)
        {
            GrantCounterResult moved;
            if (result.Outcome == LaunchOutcome.Started && result.Session is { } id && sessions.GrantTarget(id) is { } target
                && (result.Launched?.Permissions ?? launcher.LaunchStatuses().FirstOrDefault(s => s.Session == id)?.Permissions) is { } used && used == context.Request!.Permissions)
            { moved = counters.Commit(context.Input.Request, target, used); }
            else if (result.Outcome is LaunchOutcome.Refused or LaunchOutcome.Failed or LaunchOutcome.Busy)
            { moved = counters.Complete(context.Input.Request, result.Outcome == LaunchOutcome.Failed ? GrantLaunchOutcome.Failed : GrantLaunchOutcome.Refused, Reason(result)); }
            else { moved = counters.Abandon(context.Input.Request, false); }
            if (!moved.Accepted) { return Receipt(context, new(LaunchOutcome.Unconfirmed), null, "audit-unavailable"); }
        }
        var recorded = counters.Lookup(context.Input.Grant, context.Input.Request, context.Subject, context.Binding);
        return recorded.Problem == GrantCounterProblem.Duplicate ? Recorded(context, recorded) : Receipt(context, result, null, Reason(result));
    }
    FleetLaunchReceipt Recorded(FleetLaunchContext context, GrantCounterResult record)
    {
        var row = record.Reservation!;
        if (row.Caps is null) { return new(context.Input.Request, row.Grant, row.Generation,
            new(row.Outcome switch { GrantLaunchOutcome.Refused => LaunchOutcome.Refused,
                GrantLaunchOutcome.Failed => LaunchOutcome.Failed, _ => LaunchOutcome.Unconfirmed }),
            null, false, null, null, "policy"); }
        var outcome = row.Outcome switch { GrantLaunchOutcome.Started => LaunchOutcome.Started, GrantLaunchOutcome.Refused => LaunchOutcome.Refused,
            GrantLaunchOutcome.Failed => LaunchOutcome.Failed, _ => LaunchOutcome.Unconfirmed };
        return new(context.Input.Request, row.Grant, row.Generation, new(outcome) { Session = row.Target?.Session },
            row.Permissions, row.Target is not null && row.Permissions is "ask" or "allowAll", record.Usage, row.Caps, row.Reason);
    }
    internal static FleetLaunchReceipt Receipt(FleetLaunchContext? context, LaunchResult result, FleetLaunchInput? input, string? reason) =>
        new((input ?? context?.Input)?.Request ?? Guid.Empty, (input ?? context?.Input)?.Grant ?? Guid.Empty, context?.Grant?.Generation ?? 0,
            result, null, false, null, context?.Caps, reason);
    static string Reason(LaunchResult result) => result.Reason is "scope" or "policy" or "kill-switch" ? result.Reason
        : result.Reason is { } reason && reason.StartsWith("grant-", StringComparison.Ordinal) ? reason
        : result.Reason?.Contains("audit", StringComparison.OrdinalIgnoreCase) == true ? "audit-unavailable"
        : result.Reason?.Contains("fleetName", StringComparison.Ordinal) == true ? "name-gate" : "policy";
    static string CounterReason(GrantCounterResult result) => result.Problem switch
    { GrantCounterProblem.Full => "duplicate-store-full", GrantCounterProblem.Binding => "duplicate-id",
        GrantCounterProblem.Hourly or GrantCounterProblem.Daily or GrantCounterProblem.Live => "grant-exhausted",
        GrantCounterProblem.Clock => "grant-expired", _ => StoreReason(result.StoreProblem) };
    static string StoreReason(GrantStoreProblem problem) => problem switch
    { GrantStoreProblem.NotFound or GrantStoreProblem.Missing => "grant-missing", GrantStoreProblem.Revoked => "grant-revoked",
        GrantStoreProblem.Expired or GrantStoreProblem.NotYetValid => "grant-expired", _ => "audit-unavailable" };
    static GrantProcessObservation Observe(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? new(GrantProcessStatus.Exited)
                : new(GrantProcessStatus.Live, new(pid, new DateTimeOffset(process.StartTime.ToUniversalTime())));
        }
        catch (ArgumentException) { return new(GrantProcessStatus.Exited); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { return new(GrantProcessStatus.Unknown); }
    }
}
