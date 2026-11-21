using System.Collections.Immutable;

namespace AgentBridge.App.Core;

// Draft values only: not a signed grant, authenticated policy or proof of person confirmation.
public sealed record GrantIssuanceDraft(
    string Account,
    string AudienceDevice,
    string AudienceProfile,
    ImmutableArray<GrantIssuanceSubject> Subjects,
    ImmutableArray<string> Actions,
    GrantIssuanceScope Scope,
    GrantIssuanceCaps Caps,
    DateTimeOffset NotBefore,
    DateTimeOffset? ExpiresAt,
    bool AllowAllLaunch = false);

public sealed record GrantIssuanceSubject(
    string Account,
    string SourceHost,
    string Session,
    string Credential,
    int ProcessId,
    DateTimeOffset ProcessStartedAt);

public sealed record GrantIssuanceScope(
    ImmutableArray<string> Machines,
    ImmutableArray<string> Workspaces,
    ImmutableArray<string> Agents,
    ImmutableArray<string> Models,
    ImmutableArray<string> Profiles,
    ImmutableArray<string> Efforts,
    ImmutableArray<string> Contexts,
    ImmutableArray<string> Permissions);

public sealed record GrantIssuanceCaps(int Hourly, int Daily, int Live);

// G7b supplies a receiver-owned snapshot; this DTO does not authenticate host policy.
public sealed record GrantIssuancePolicy(
    string Account, string Device, string Profile,
    GrantIssuanceScope Scope, GrantIssuanceCaps Caps,
    bool IsCurrent, bool LaunchAllowed, bool StopAllowed, bool AllowAllLaunch = false);
