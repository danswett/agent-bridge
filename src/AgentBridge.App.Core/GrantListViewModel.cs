using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.AgentEnvelope;
using AgentBridge.App.Core.Grants;

namespace AgentBridge.App.Core;

public sealed class GrantListRow
{
    internal GrantListRow(GrantSnapshot snapshot, string receiver)
    {
        Snapshot = snapshot;
        var d = snapshot.Document;
        if (!Guid.TryParse(Text(d, "id"), out Id) || Get(d, "generation").ValueKind != JsonValueKind.Number
            || !Get(d, "generation").TryGetInt64(out Generation)) { throw new InvalidDataException("invalid grant identity"); }
        Account = Text(d, "account"); Profile = Text(Get(d, "audience"), "profile");
        Receiver = Text(Get(d, "audience"), "device");
        if (Id == Guid.Empty || Generation <= 0 || Receiver != receiver) { throw new InvalidDataException("grant identity mismatch"); }
        var caps = Get(d, "caps");
        HourCap = Count(caps, "hourly"); DayCap = Count(caps, "daily"); LiveCap = Count(caps, "live");
        if (HourCap is <= 0 or > 1000 || DayCap is <= 0 or > 5000 || LiveCap is <= 0 or > 100 || HourCap > DayCap
            || snapshot.Counters is not { Hour: >= 0, Day: >= 0, Pending: >= 0, Live: >= 0 }
            || snapshot.Counters.PendingRequests.IsDefault || snapshot.Counters.LiveSessions.IsDefault) { throw new InvalidDataException("invalid receiver counters"); }
        var issuer = Get(d, "issuer");
        var subjects = Items(Get(d, "subjects")).Select(s => $"{Display(Text(s, "account"))}/{Display(Text(s, "sourceHost"))}/{Display(Text(s, "session"))} process {Count(Get(s, "processInstance"), "pid")} since {Time(Get(s, "processInstance"), "startedAt").ToLocalTime():g}");
        var scope = Get(d, "scope");
        Details = $"Issuer: {Display(Text(issuer, "account"))} / {Display(Text(issuer, "device"))}\nSubjects: {string.Join(", ", subjects)}\nActions: {List(Get(d, "actions"))}\nScope: "
            + string.Join("; ", new[] { "machines", "workspaces", "agents", "models", "profiles", "efforts", "contexts", "permissions" }.Select(name => $"{name}: {List(Get(scope, name))}"))
            + $"\nExpires: {Time(d, "expiresAt").ToLocalTime():g}\nLast caller: {Caller(snapshot.Counters.LastCaller)}; outcome: {Outcome(snapshot.Counters.LastOutcome)}";
    }
    internal GrantSnapshot Snapshot;
    internal Guid Id;
    internal long Generation;
    internal string Receiver, Account, Profile;
    internal int HourCap, DayCap, LiveCap;
    internal bool Unavailable, RevocationPending;
    internal GrantRevokeReceipt? Tombstone;
    public string Title => $"{Id} / {Generation} on {Receiver}";
    public string Details { get; }
    public string State => (Tombstone is not null ? "Revoked" : RevocationPending ? "Revocation pending" : "Receiver verified")
        + (Unavailable || Snapshot.Stale ? " (stale: receiver offline or unavailable)" : "");
    public string Counters => $"{Snapshot.Counters.Hour}/{HourCap} hour; {Snapshot.Counters.Day}/{DayCap} day; {(long)Snapshot.Counters.Pending + Snapshot.Counters.Live}/{LiveCap} occupied (pending {Snapshot.Counters.Pending}, live {Snapshot.Counters.Live})\nRemaining: {Math.Max(0, HourCap - Snapshot.Counters.Hour)} hour; {Math.Max(0, DayCap - Snapshot.Counters.Day)} day; {Math.Max(0, LiveCap - (long)Snapshot.Counters.Pending - Snapshot.Counters.Live)} live\nAs of {Snapshot.Counters.ObservedAt.ToLocalTime():g}";
    public string InFlight => string.Join(", ", Snapshot.Counters.PendingRequests.Concat(Snapshot.Counters.LiveSessions).Concat(Tombstone?.InFlightRequests ?? []).Distinct().Select(Caller));
    public string Receipt => Tombstone is { } receipt ? $"Tombstone receipt: {receipt.Digest}" : "";
    public bool CanRevoke => !Unavailable && !Snapshot.Stale && !RevocationPending && Tombstone is null;
    internal static JsonElement Get(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) ? value : throw new InvalidDataException("incomplete grant snapshot");
    internal static string Text(JsonElement e, string name) => Get(e, name) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : throw new InvalidDataException("missing grant identifier");
    static IEnumerable<JsonElement> Items(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : throw new InvalidDataException("invalid grant list");
    static string List(JsonElement e) => string.Join(", ", Items(e).Select(v => v.ValueKind == JsonValueKind.String ? Display(v.GetString()!) : "Unknown"));
    static int Count(JsonElement e, string name) => Get(e, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var count) ? count : throw new InvalidDataException("invalid grant allocation");
    static DateTimeOffset Time(JsonElement d, string name) => Get(d, name) is { ValueKind: JsonValueKind.String } e && e.TryGetDateTimeOffset(out var time) ? time : throw new InvalidDataException("invalid grant time");
    static string Display(string value) => value.Length is > 0 and <= 256 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '[' or ']') ? value : "Unknown";
    static string Caller(string? value) => value is { Length: > 0 and <= 256 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '/' or '-' or '_' or '.') ? value : "Unknown";
    static string Outcome(string? value) => value is "delivered" or "started" or "stopped" or "refused" or "unconfirmed" or "reserved" or "pending" or "failed" or "exited" or "issued" or "revoked" or "committed" or "abandoned" or "notLaunched" ? value : "Unknown";
}

public sealed class GrantListViewModel(IGrantListReceiver receiver, IVerifiedGrantReader? verifiedReader = null) : Observable
{
    readonly IVerifiedGrantReader reader = verifiedReader ?? receiver as IVerifiedGrantReader ?? new UnavailableVerifiedGrantReader();
    IReadOnlyList<GrantListRow> rows = [];
    string error = "";
    string? readProblem = "unavailable";
    string? verifiedReceiver;
    FleetGrantReadProblem verifiedProblem = FleetGrantReadProblem.Unavailable;
    bool g7Ready;
    bool busy;
    public GrantListViewModel() : this(new UnavailableReceiver()) { }
    public IReadOnlyList<GrantListRow> Rows => rows;
    public string Error => error;
    public bool Busy => busy;
    bool ReceiverAvailable => receiver.Online && receiver.Problem is null;
    bool VerifiedReceiverAvailable => ReceiverAvailable && verifiedReceiver is not null && verifiedReceiver == receiver.Receiver;
    public bool Available => VerifiedReceiverAvailable && verifiedProblem == FleetGrantReadProblem.None && readProblem is null;
    public bool CanIssueFirst => !busy && VerifiedReceiverAvailable && verifiedProblem == FleetGrantReadProblem.RotationRequired && g7Ready;
    public string FirstIssueNotice => CanIssueFirst ? "First issue rotates this profile after person confirmation. Only a verified receiver receipt confirms rotation." : "";
    void Availability()
    {
        foreach (var row in rows) { row.Unavailable = !Available || row.Receiver != receiver.Receiver; }
        error = (receiver.Problem ?? readProblem) is "RotationRequired" or "rotation-required" ? "RotationRequired: receiver list unavailable." + (CanIssueFirst ? "" : " Grant operations remain blocked.")
            : !Available ? "Receiver adapter unavailable or offline. Last verified counters are stale; no new receipt is confirmed." : "";
    }
    public async Task RefreshAsync()
    {
        if (busy) { return; }
        verifiedReceiver = null; verifiedProblem = FleetGrantReadProblem.Unavailable; g7Ready = false;
        Availability();
        if (!ReceiverAvailable) { RaiseAll(); return; }
        busy = true; RaiseAll();
        try
        {
            var readReceiver = receiver.Receiver;
            var verified = await reader.ReadAsync();
            if (readReceiver != receiver.Receiver) { readProblem = "context-changed"; Availability(); return; }
            if (!ReceiverAvailable) { Availability(); return; }
            if (verified is null) { throw new InvalidDataException("missing verified receiver result"); }
            verifiedReceiver = readReceiver; verifiedProblem = verified.Problem; g7Ready = verified.G7Ready;
            var result = verified.Data;
            if (result is null) { throw new InvalidDataException("missing receiver result"); }
            if (verifiedProblem != FleetGrantReadProblem.None || result.Problem is not null)
            {
                readProblem = verifiedProblem == FleetGrantReadProblem.RotationRequired || result.Problem is "RotationRequired" or "rotation-required" ? "RotationRequired" : "unavailable";
                Availability(); return;
            }
            if (result.Grants.IsDefault) { throw new InvalidDataException("missing receiver snapshot"); }
            var previous = rows.ToDictionary(r => (r.Id, r.Generation, r.Receiver));
            var refreshed = result.Grants.Select(snapshot =>
            {
                var row = new GrantListRow(snapshot, readReceiver);
                if (previous.Remove((row.Id, row.Generation, row.Receiver), out var old)) { row.Tombstone = old.Tombstone; row.RevocationPending = old.RevocationPending; }
                return row;
            }).ToList();
            if (refreshed.Select(r => (r.Id, r.Generation, r.Receiver)).Distinct().Count() != refreshed.Count) { throw new InvalidDataException("duplicate grant snapshot"); }
            refreshed.AddRange(previous.Values.Where(row => row.Tombstone is not null || row.RevocationPending).Select(row => { row.Snapshot = row.Snapshot with { Stale = true }; return row; }));
            rows = refreshed; readProblem = null; Availability();
        }
        catch (Exception e) when (UnavailableError(e))
        {
            verifiedReceiver = null; verifiedProblem = FleetGrantReadProblem.Unavailable; g7Ready = false;
            readProblem ??= "unavailable";
            error = readProblem == "RotationRequired" ? "RotationRequired: person key rotation is required."
                : "Receiver read unavailable. Last verified counters are stale.";
            foreach (var row in rows) { row.Unavailable = true; }
        }
        finally { busy = false; Availability(); RaiseAll(); }
    }
    public async Task RevokeAsync(GrantListRow row, Func<string, Task<bool>> personConfirm)
    {
        if (busy) { return; }
        Availability();
        if (receiver.Receiver != row.Receiver) { readProblem = "context-changed"; Availability(); }
        if (!rows.Contains(row) || !row.CanRevoke) { RaiseAll(); return; }
        busy = true; RaiseAll();
        try
        {
            var preview = await receiver.PrepareRevokeAsync(row.Id, row.Generation);
            if (preview is null || preview.Document is null || preview.Digest is null) { throw new InvalidDataException("missing revoke preview"); }
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preview.Document)));
            var digest = preview.Digest;
            if (preview.Id != row.Id || preview.Generation != row.Generation || preview.Receiver != row.Receiver
                || preview.Account != row.Account || preview.Profile != row.Profile || preview.Action != "grant.revoke"
                || !string.Equals(digest, hash, StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("unmatched revoke preview"); }
            if (!Available || !await personConfirm($"{row.Title}\n{row.Details}\nRevoke digest: {digest}")) { Availability(); return; }
            if (!Available || receiver.Receiver != row.Receiver) { Availability(); return; }
            row.RevocationPending = true; RaiseAll();
            var receipt = await receiver.ConfirmRevokeAsync(row.Id, row.Generation, digest);
            if (receipt is null || receipt.Id != preview.Id || receipt.Generation != preview.Generation || receipt.Receiver != preview.Receiver
                || receipt.Account != preview.Account || receipt.Profile != preview.Profile || receipt.Action != preview.Action
                || receipt.Digest != digest || !receipt.Tombstone || receipt.InFlightRequests.IsDefault)
            { throw new InvalidDataException("unmatched tombstone receipt"); }
            row.Tombstone = receipt; row.RevocationPending = false; Availability();
        }
        catch (Exception e) when (UnavailableError(e))
        { readProblem = "unavailable"; Availability(); error = row.RevocationPending ? "Revocation pending. No matching tombstone receipt was confirmed; in-flight sessions may continue." : "Revoke unavailable. No revocation is confirmed."; }
        finally { busy = false; RaiseAll(); }
    }
    static bool UnavailableError(Exception e) => e is IOException or InvalidDataException or TimeoutException or JsonException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException;
    sealed class UnavailableReceiver : IGrantListReceiver
    {
        public string Receiver => "";
        public bool Online => false;
        public string? Problem => "unavailable";
        public Task<GrantReadResult> ReadAsync(CancellationToken ct = default) => Task.FromResult(new GrantReadResult([], Problem));
        public Task<GrantRevokePreview> PrepareRevokeAsync(Guid id, long generation, CancellationToken ct = default) => throw new IOException("receiver adapter unavailable");
        public Task<GrantRevokeReceipt> ConfirmRevokeAsync(Guid id, long generation, string digest, CancellationToken ct = default) => throw new IOException("receiver adapter unavailable");
    }
}
