using System.Collections.Immutable;
using System.Text.Json;

namespace AgentBridge.App.Core.Grants;

public sealed record GrantAllocation(int Hourly, int Daily, int Live);
public sealed record GrantIssuePreview(Guid Id, long Generation, string Receiver, string Profile,
    string Document, string Digest, GrantAllocation Allocation, string EffectivePolicy,
    ImmutableArray<string> UnavailableActions);
public sealed record GrantReceipt(Guid Id, long Generation, string Receiver, string Digest);
public sealed record GrantReceiverCounters(int Hour, int Day, int Pending, int Live,
    ImmutableArray<string> Requests, string? LastCaller, string? LastOutcome, DateTimeOffset ObservedAt,
    ImmutableArray<string> PendingRequests, ImmutableArray<string> LiveSessions);
public sealed record GrantSnapshot(JsonElement Document, GrantReceiverCounters Counters, bool Stale);
public sealed record GrantReadResult(ImmutableArray<GrantSnapshot> Grants, string? Problem);
public sealed record GrantRevokePreview(Guid Id, long Generation, string Receiver, string Account,
    string Profile, string Action, string Document, string Digest);
public sealed record GrantRevokeReceipt(Guid Id, long Generation, string Receiver, string Account,
    string Profile, string Action, string Digest,
    bool Tombstone, ImmutableArray<string> InFlightRequests);
public sealed record GrantPrepareResult(GrantIssuePreview? Preview, string? Problem);
public sealed record GrantIssueResult(GrantReceipt? Receipt, string? Problem);

public interface IGrantListReceiver
{
    string Receiver { get; }
    bool Online { get; }
    string? Problem { get; }
    Task<GrantReadResult> ReadAsync(CancellationToken ct = default);
    Task<GrantRevokePreview> PrepareRevokeAsync(Guid id, long generation, CancellationToken ct = default);
    Task<GrantRevokeReceipt> ConfirmRevokeAsync(Guid id, long generation, string digest, CancellationToken ct = default);
}

public interface IGrantIssueReceiver : IGrantListReceiver
{
    Task<GrantPrepareResult> PrepareIssueAsync(JsonElement document, CancellationToken ct = default);
    Task<GrantIssueResult> ConfirmIssueAsync(Guid id, long generation, string digest, CancellationToken ct = default);
}
