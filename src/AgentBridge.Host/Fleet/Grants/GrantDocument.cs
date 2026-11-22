using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.AgentEnvelope;
using AgentBridge.Protocol;
using AgentBridge.Roster;

namespace AgentBridge.Host.Fleet.Grants;

public sealed record ProcessInstance(int Pid, DateTimeOffset StartedAt);
public sealed record GrantSubject(string Account, string SourceHost, string Session, string Credential, ProcessInstance ProcessInstance);
public sealed record GrantIssuer(string Account, string Device, string PersonProof, DateTimeOffset IssuedAt, string Digest);
public sealed record GrantAudience(string Device, string Profile);
public sealed record GrantRole(string Name, GrantSubject Subject);
public sealed record StopTarget(string Session, ProcessInstance ProcessInstance);
public sealed record GrantLaunch(Guid GrantId, GrantSubject Subject, StopTarget Target);
public sealed record GrantCaps(int Hourly, int Daily, int Live);
public sealed record GrantScope(ImmutableArray<string> Machines, ImmutableArray<string> Workspaces,
    ImmutableArray<string> Agents, ImmutableArray<string> Models, ImmutableArray<string> Profiles,
    ImmutableArray<string> Efforts, ImmutableArray<string> Contexts, ImmutableArray<string> Permissions);
public sealed record GrantDocument(int Version, Guid Id, string Account, GrantIssuer Issuer, GrantAudience Audience,
    ImmutableArray<GrantSubject> Subjects, ImmutableArray<string> Actions, GrantScope Scope, GrantCaps Caps,
    DateTimeOffset NotBefore, long Generation, DateTimeOffset? ExpiresAt = null,
    ImmutableArray<GrantRole> Roles = default, ImmutableArray<StopTarget> StopTargets = default, bool AllowAllLaunch = false);

// Inputs are authenticated, receiver-owned snapshots; this pure layer does not verify person proof or inspect processes.
public sealed record HostPolicySnapshot(string Account, GrantAudience Audience, GrantSubject AuthenticatedCaller,
    GrantScope Scope, GrantCaps Caps, bool LaunchAllowed, bool StopAllowed, bool AllowAll = false,
    ImmutableArray<GrantLaunch> Launches = default, ImmutableArray<StopTarget> StopTargets = default);
public sealed record GrantRequest(GrantSubject Subject, string Action, string Machine, string Workspace, string Agent,
    string? Model = null, string? Profile = null, string? Effort = null, string? Context = null,
    string? Permission = null, StopTarget? Target = null);
public sealed record EffectiveLaunch(string Model, string Profile, string Effort, string Context, string Permission, GrantCaps Caps);
public enum GrantRefusal
{
    None, Malformed, Version, Identifier, Caps, Lifetime, Account, Audience, Subject,
    NotYetValid, Expired, Action, Policy, Scope, Permission, StopTarget
}
public sealed record GrantDecision(GrantRefusal Reason, EffectiveLaunch? Launch = null)
{
    public bool Allowed => Reason == GrantRefusal.None;
}
public sealed record GrantValidation(GrantRefusal Reason, GrantDocument? Grant = null)
{
    public bool Valid => Reason == GrantRefusal.None;
}

public static class GrantValidator
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 12,
    };
    public static GrantValidation Parse(string? json, GrantScope knownOptions)
    {
        if (json is null || json.Length > 64 * 1024) { return new(GrantRefusal.Malformed); }
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 12 });
            if (!UniqueFields(document.RootElement)) { return new(GrantRefusal.Malformed); }
            return Validate(document.RootElement.Deserialize<GrantDocument>(Json), knownOptions);
        }
        catch (JsonException) { return new(GrantRefusal.Malformed); }
    }
    public static GrantValidation Validate(GrantDocument? grant, GrantScope knownOptions)
    {
        var shape = ValidateShape(grant);
        return !shape.Valid ? shape : !ValidScope(knownOptions) || !Known(grant!.Scope, knownOptions)
            ? new(GrantRefusal.Identifier) : shape;
    }
    internal static GrantValidation ValidateShape(GrantDocument? grant)
    {
        if (grant is null) { return new(GrantRefusal.Malformed); }
        if (grant.Version != 1) { return new(GrantRefusal.Version); }
        if (grant.Id == Guid.Empty || grant.Generation <= 0 || !Account(grant.Account)
            || grant.Issuer is not { } issuer || issuer.Account != grant.Account || !Device(issuer.Device)
            || !Bytes(issuer.PersonProof, null) || !Hex(issuer.Digest, 64)
            || grant.Audience is not { } audience || !Device(audience.Device) || !Name(audience.Profile)
            || !List(grant.Subjects, s => Subject(s) && s.Account == grant.Account)
            || (!grant.Roles.IsDefault && (!List(grant.Roles, r => r is not null && Name(r.Name, true)
                && grant.Subjects.Contains(r.Subject), true) || grant.Roles.Select(r => r.Name).Distinct().Count() != grant.Roles.Length))
            || (!grant.StopTargets.IsDefault && !List(grant.StopTargets, Target, true)))
        { return new(GrantRefusal.Identifier); }
        if (!List(grant.Actions, a => a is "launch" or "stop")) { return new(GrantRefusal.Action); }
        if (!ValidScope(grant.Scope) || !grant.Scope.Machines.Contains(audience.Device)) { return new(GrantRefusal.Scope); }
        if (!Positive(grant.Caps) || grant.Caps is not { Hourly: <= 1000, Daily: <= 5000, Live: <= 100 }
            || grant.Caps.Hourly > grant.Caps.Daily) { return new(GrantRefusal.Caps); }
        grant = grant with
        {
            Issuer = issuer with { IssuedAt = issuer.IssuedAt.ToUniversalTime() },
            NotBefore = grant.NotBefore.ToUniversalTime(), ExpiresAt = grant.ExpiresAt?.ToUniversalTime()
        };
        // Reserve the full maximum lifetime horizon before doing expiry arithmetic.
        var issuedAt = grant.Issuer.IssuedAt;
        if (issuedAt == default || issuedAt > DateTimeOffset.MaxValue.AddHours(-24)
            || grant.NotBefore < issuedAt || Expiry(grant) <= grant.NotBefore
            || Expiry(grant) - issuedAt > TimeSpan.FromHours(24))
        { return new(GrantRefusal.Lifetime); }
        return new(GrantRefusal.None, grant);
    }
    internal static DateTimeOffset Expiry(GrantDocument grant) => grant.ExpiresAt ?? grant.Issuer.IssuedAt.AddHours(8);
    internal static bool Positive(GrantCaps? caps) => caps is { Hourly: > 0, Daily: > 0, Live: > 0 };
    internal static bool Subject(GrantSubject? s) => s is not null && Account(s.Account) && Device(s.SourceHost)
        && Session(s.Session) && Bytes(s.Credential, 16) && Process(s.ProcessInstance);
    internal static bool Target(StopTarget? t) => t is not null && Session(t.Session) && Process(t.ProcessInstance);
    static bool Process(ProcessInstance? p) => p is { Pid: > 0 } && p.StartedAt != default;
    static bool Account(string? s) => s is not null && EnvelopeChecks.IsAccountId(s);
    static bool Device(string? s) => Hex(s, 32);
    static bool Hex(string? s, int length) => s is not null && s.Length == length && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static bool Session(string? s) => (Guid.TryParseExact(s, "D", out var id) || Guid.TryParseExact(s, "N", out id)) && id != Guid.Empty;
    static bool Bytes(string? s, int? length) => s is { Length: > 0 and <= 4096 }
        && Codec.TryBase64Url(s, out var bytes) && bytes.Length > 0 && (length is null || bytes.Length == length);
    static bool Name(string? s, bool spaces = false) => s is { Length: > 0 and <= 128 }
        && char.IsAsciiLetterOrDigit(s[0]) && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' || (spaces && c == ' '));
    static bool List<T>(ImmutableArray<T> values, Func<T, bool> valid, bool empty = false) => !values.IsDefault
        && values.Length <= 64 && (empty || values.Length > 0) && values.All(valid) && values.Distinct().Count() == values.Length;
    internal static bool ValidScope(GrantScope? s) => s is not null && List(s.Machines, Device)
        && List(s.Workspaces, v => Name(v, true)) && List(s.Agents, a => LaunchProtocol.Agents.Contains(a, StringComparer.Ordinal))
        && List(s.Models, LaunchProtocol.IsTuning) && List(s.Profiles, v => Name(v))
        && List(s.Efforts, LaunchProtocol.IsTuning) && List(s.Contexts, LaunchProtocol.IsTuning)
        && List(s.Permissions, p => p is LaunchProtocol.Ask or LaunchProtocol.AllowAll);
    static bool Known(GrantScope scope, GrantScope options) =>
        new[] { (scope.Machines, options.Machines), (scope.Workspaces, options.Workspaces), (scope.Agents, options.Agents),
            (scope.Models, options.Models), (scope.Profiles, options.Profiles), (scope.Efforts, options.Efforts),
            (scope.Contexts, options.Contexts), (scope.Permissions, options.Permissions) }
        .All(pair => pair.Item1.All(v => pair.Item2.Contains(v, StringComparer.Ordinal)));
    static bool UniqueFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) { return element.EnumerateArray().All(UniqueFields); }
        if (element.ValueKind != JsonValueKind.Object) { return true; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().All(p => names.Add(p.Name) && UniqueFields(p.Value));
    }
}

public static class GrantEvaluator
{
    public static GrantDecision Evaluate(GrantDocument grant, HostPolicySnapshot policy, GrantRequest request, DateTimeOffset now)
    {
        var validation = GrantValidator.ValidateShape(grant);
        if (!validation.Valid) { return new(validation.Reason); }
        grant = validation.Grant!;
        if (policy is null || request is null || !GrantValidator.ValidScope(policy.Scope) || !GrantValidator.Positive(policy.Caps))
        { return new(GrantRefusal.Policy); }
        if (policy.Account != grant.Account || request.Subject?.Account != grant.Account) { return new(GrantRefusal.Account); }
        if (policy.Audience != grant.Audience) { return new(GrantRefusal.Audience); }
        if (!GrantValidator.Subject(request.Subject) || request.Subject != policy.AuthenticatedCaller || !grant.Subjects.Contains(request.Subject))
        { return new(GrantRefusal.Subject); }
        if (now < grant.NotBefore) { return new(GrantRefusal.NotYetValid); }
        if (now >= GrantValidator.Expiry(grant)) { return new(GrantRefusal.Expired); }
        if (request.Action is not ("launch" or "stop") || !grant.Actions.Contains(request.Action)) { return new(GrantRefusal.Action); }
        if (!(request.Action == "launch" ? policy.LaunchAllowed : policy.StopAllowed)) { return new(GrantRefusal.Policy); }
        if (request.Machine != grant.Audience.Device || !Both(request.Machine, grant.Scope.Machines, policy.Scope.Machines))
        { return new(GrantRefusal.Scope); }
        if ((request.Action == "launch" && request.Target is not null) || !Both(request.Workspace, grant.Scope.Workspaces, policy.Scope.Workspaces)
            || !Both(request.Agent, grant.Scope.Agents, policy.Scope.Agents)) { return new(GrantRefusal.Scope); }
        if (request.Action == "stop")
        {
            var target = request.Target;
            var own = !policy.Launches.IsDefault && policy.Launches.Any(l => l is not null && l.GrantId == grant.Id
                && l.Subject == request.Subject && l.Target == target);
            var named = !grant.StopTargets.IsDefault && grant.StopTargets.Contains(target!);
            return GrantValidator.Target(target) && !policy.StopTargets.IsDefault && policy.StopTargets.Contains(target!) && (own || named)
                ? new(GrantRefusal.None) : new(GrantRefusal.StopTarget);
        }
        var model = Resolve(request.Model, grant.Scope.Models, policy.Scope.Models);
        var profile = Resolve(request.Profile, grant.Scope.Profiles, policy.Scope.Profiles);
        var effort = Resolve(request.Effort, grant.Scope.Efforts, policy.Scope.Efforts);
        var context = Resolve(request.Context, grant.Scope.Contexts, policy.Scope.Contexts);
        var permissions = policy.Scope.Permissions.Where(p => p != LaunchProtocol.AllowAll || policy.AllowAll).ToImmutableArray();
        var permission = Resolve(request.Permission ?? (Both(LaunchProtocol.Ask, grant.Scope.Permissions, permissions) ? LaunchProtocol.Ask : null),
            grant.Scope.Permissions, permissions);
        if (model is null || profile is null || effort is null || context is null) { return new(GrantRefusal.Scope); }
        if (permission is null) { return new(GrantRefusal.Permission); }
        var caps = new GrantCaps(Math.Min(grant.Caps.Hourly, policy.Caps.Hourly),
            Math.Min(grant.Caps.Daily, policy.Caps.Daily), Math.Min(grant.Caps.Live, policy.Caps.Live));
        return new(GrantRefusal.None, new(model, profile, effort, context, permission, caps));
    }
    static bool Both(string value, ImmutableArray<string> grant, ImmutableArray<string> policy) =>
        grant.Contains(value, StringComparer.Ordinal) && policy.Contains(value, StringComparer.Ordinal);
    static string? Resolve(string? value, ImmutableArray<string> grant, ImmutableArray<string> policy) =>
        value is null ? policy.FirstOrDefault(v => grant.Contains(v, StringComparer.Ordinal)) : Both(value, grant, policy) ? value : null;
}
