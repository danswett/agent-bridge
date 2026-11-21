using System.Collections.Immutable;

namespace AgentBridge.App.Core;

// Confirmation is UI state, not person proof. G7b must still use the trusted host issuance path.
public sealed class GrantIssuanceViewModel : Observable
{
    readonly Func<DateTimeOffset> now;
    public GrantIssuanceDraft Draft { get; private set; }
    public GrantIssuancePolicy Policy { get; private set; }
    public bool IsPersonConfirmed { get; private set; }

    public GrantIssuanceViewModel(GrantIssuanceDraft draft, GrantIssuancePolicy policy,
        Func<DateTimeOffset>? now = null)
    {
        Draft = draft ?? throw new ArgumentNullException(nameof(draft));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public void Update(GrantIssuanceDraft draft, GrantIssuancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(policy);
        Draft = draft;
        Policy = policy;
        IsPersonConfirmed = false;
        RaiseAll();
    }

    public bool CanConfirm => ValidationMessage.Length == 0;
    public bool CanSubmit => IsPersonConfirmed && CanConfirm;
    public string ConfirmationText => IsPersonConfirmed
        ? "Confirmed for submission only; not issued. The host must verify person authority and return an issuance receipt."
        : "Person confirmation required; not issued. No authority to answer, reply or trust prompts.";

    public void ConfirmByPerson()
    {
        var error = ValidationMessage;
        if (error.Length != 0) { throw new InvalidOperationException(error); }
        IsPersonConfirmed = true;
        RaiseAll();
    }

    public ImmutableArray<string> EffectiveActions => Values(Draft.Actions)
        .Where(a => a == "launch" && Policy.LaunchAllowed || a == "stop" && Policy.StopAllowed).ToImmutableArray();
    public GrantIssuanceCaps EffectiveCaps => new(Math.Min(Draft.Caps.Hourly, Policy.Caps.Hourly),
        Math.Min(Draft.Caps.Daily, Policy.Caps.Daily), Math.Min(Draft.Caps.Live, Policy.Caps.Live));
    public bool EffectiveAllowAllLaunch => Draft.AllowAllLaunch && Policy.AllowAllLaunch
        && Policy.LaunchAllowed && Values(Draft.Actions).Contains("launch")
        && Values(Draft.Scope.Permissions).Contains("allowAll") && Values(Policy.Scope.Permissions).Contains("allowAll");
    public string AllowAllLaunchText => $"Allow-all launch: requested {(Draft.AllowAllLaunch ? "ON" : "OFF")}; "
        + $"effective {(EffectiveAllowAllLaunch ? "ON" : "OFF")}. Requires both the grant flag and receiving-host policy.";
    public GrantIssuanceScope EffectiveScope => new(
        Intersect(Draft.Scope.Machines, Policy.Scope.Machines),
        Intersect(Draft.Scope.Workspaces, Policy.Scope.Workspaces),
        Intersect(Draft.Scope.Agents, Policy.Scope.Agents),
        Intersect(Draft.Scope.Models, Policy.Scope.Models),
        Intersect(Draft.Scope.Profiles, Policy.Scope.Profiles),
        Intersect(Draft.Scope.Efforts, Policy.Scope.Efforts),
        Intersect(Draft.Scope.Contexts, Policy.Scope.Contexts),
        Intersect(Draft.Scope.Permissions, Policy.Scope.Permissions)
            .Where(p => p != "allowAll" || EffectiveAllowAllLaunch).ToImmutableArray());

    public string ValidationMessage
    {
        get
        {
            if (!Policy.IsCurrent) { return "Receiving-host policy is unavailable or stale."; }
            if (!Text(Draft.Account) || Draft.Account != Policy.Account || Draft.AudienceDevice != Policy.Device
                || Draft.AudienceProfile != Policy.Profile) { return "Account or receiving-host audience does not match."; }
            if (Draft.Subjects.IsDefaultOrEmpty || Draft.Subjects.Length > 64
                || Draft.Subjects.Distinct().Count() != Draft.Subjects.Length
                || Draft.Subjects.Any(s => s is null || s.Account != Draft.Account || !Text(s.SourceHost)
                    || !Guid.TryParse(s.Session, out var id) || id == Guid.Empty || !Text(s.Credential)
                    || s.ProcessId <= 0 || s.ProcessStartedAt == default))
            { return "Select explicit account, host, session, credential and process-bound subjects."; }
            if (!Selection(Draft.Actions) || Draft.Actions.Any(a => a is not ("launch" or "stop")))
            { return "Select launch or stop explicitly; other authority is excluded."; }
            if (!Caps(Draft.Caps) || !Caps(Policy.Caps)) { return "Enter valid positive hourly, daily and live caps."; }
            var current = now();
            if (Draft.NotBefore == default || Draft.NotBefore > current || Draft.ExpiresAt is not { } expiry
                || expiry <= current || expiry <= Draft.NotBefore || expiry - Draft.NotBefore > TimeSpan.FromHours(24))
            { return "Select a current validity window and explicit expiry within 24 hours."; }
            if (!Scope(Draft.Scope) || !Scope(Policy.Scope, allowEmpty: true)
                || !Draft.Scope.Machines.Contains(Draft.AudienceDevice, StringComparer.Ordinal))
            { return "Select exact scope values, including the receiving host; no wildcards or defaults."; }
            if (EffectiveActions.Length != Draft.Actions.Length) { return "A selected action is unavailable under host policy."; }
            var effective = EffectiveScope;
            if (effective.Machines.IsEmpty || effective.Workspaces.IsEmpty || effective.Agents.IsEmpty
                || !effective.Machines.Contains(Draft.AudienceDevice, StringComparer.Ordinal)
                || (Draft.Actions.Contains("launch") && (effective.Models.IsEmpty || effective.Profiles.IsEmpty
                    || effective.Efforts.IsEmpty || effective.Contexts.IsEmpty || effective.Permissions.IsEmpty)))
            { return "Selected scope is unavailable after receiving-host policy intersection."; }
            return "";
        }
    }

    public string Summary => string.Join(Environment.NewLine,
        $"Account: {Draft.Account}; receiving host: {Draft.AudienceDevice}; profile: {Draft.AudienceProfile}",
        "Subjects: " + string.Join("; ", Values(Draft.Subjects).Select(s =>
            $"{s.Account} / {s.SourceHost} / {s.Session} / PID {s.ProcessId} started {s.ProcessStartedAt.ToLocalTime():O}")),
        $"Requested actions: {Join(Draft.Actions)}; effective: {Join(EffectiveActions)}",
        $"Requested scope: {Describe(Draft.Scope)}",
        $"Effective scope: {Describe(EffectiveScope)}",
        $"Caps hourly/daily/live (including reservations): requested {Describe(Draft.Caps)}; effective {Describe(EffectiveCaps)}",
        $"Valid from {Draft.NotBefore.ToLocalTime():O} until {Draft.ExpiresAt?.ToLocalTime():O}",
        AllowAllLaunchText,
        "Stop scope: only this exact subject's launches under this grant; no existing-session exceptions.",
        "Other grants consume shared host limits. The receiver rechecks all safety gates; this preview is not authority.",
        ValidationMessage, ConfirmationText);

    static ImmutableArray<T> Values<T>(ImmutableArray<T> values) => values.IsDefault ? [] : values;
    static ImmutableArray<string> Intersect(ImmutableArray<string> requested, ImmutableArray<string> policy) =>
        Values(requested).Intersect(Values(policy), StringComparer.Ordinal).ToImmutableArray();
    static bool Text(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 4096 && !value.Contains('*') && !value.Any(char.IsControl);
    static bool Selection(ImmutableArray<string> values, bool allowEmpty = false) => !values.IsDefault
        && (allowEmpty || !values.IsEmpty) && values.Length <= 64 && values.All(Text)
        && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    static bool Scope(GrantIssuanceScope scope, bool allowEmpty = false) =>
        new[] { scope.Machines, scope.Workspaces, scope.Agents, scope.Models, scope.Profiles,
            scope.Efforts, scope.Contexts, scope.Permissions }.All(v => Selection(v, allowEmpty))
        && scope.Permissions.All(p => p is "ask" or "allowAll");
    static bool Caps(GrantIssuanceCaps caps) => caps is { Hourly: > 0 and <= 1000,
        Daily: > 0 and <= 5000, Live: > 0 and <= 100 } && caps.Hourly <= caps.Daily;
    static string Join(ImmutableArray<string> values) => values.IsDefaultOrEmpty ? "(none)" : string.Join(", ", values);
    static string Describe(GrantIssuanceCaps caps) => $"{caps.Hourly}/{caps.Daily}/{caps.Live}";
    static string Describe(GrantIssuanceScope scope) => $"machines [{Join(scope.Machines)}]; workspaces [{Join(scope.Workspaces)}]; "
        + $"agents [{Join(scope.Agents)}]; models [{Join(scope.Models)}]; profiles [{Join(scope.Profiles)}]; "
        + $"efforts [{Join(scope.Efforts)}]; contexts [{Join(scope.Contexts)}]; permissions [{Join(scope.Permissions)}]";
}
