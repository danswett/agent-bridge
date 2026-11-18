using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Roster;

namespace AgentBridge.Host;

internal sealed record CapConsumed(string Request, string Nonce, string Consent, string IssuedAt, string ExpiresAt);
internal sealed record CapEvidence(string Collection, string Frontier, string Verifiers, string ExpiresAt);

public sealed partial class HostRosterReader
{
    sealed record Replay(string Request, string IssuedAt, string ExpiresAt);
    sealed record Transition(int V, string Account, string Generation, string Genesis, string[] Heads, bool AcceptHostSign,
        string HighWater, string? Floor, string? HighestIssuedAt, Replay[] Requests,
        string? CapHighestIssuedAt = null, CapConsumed[]? Consumed = null, CapEvidence? Evidence = null);
    static readonly JsonSerializerOptions TransitionJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 16,
    };
    const int TransitionLimit = 256 * 1024, ReplayLimit = 512;
    Transition? transition;
    string TransitionPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pinPath))!, "roster-transition.json");
    readonly string pinPath;
    byte[][] TransitionHeads => transition?.Heads.Select(h => Codec.FromBase64Url(h, 32, "head")).ToArray() ?? [];

    string? ReloadTransition(RosterVerifier verified)
    {
        var (next, issue) = ReadPrivate(TransitionPath, TransitionLimit, bytes =>
        {
            if (!Utf8.IsValid(bytes)) { throw new InvalidDataException(); }
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("v", out var v)) { throw new InvalidDataException(); }
            var version = v.GetRawText();
            var extra = new[] { "capHighestIssuedAt", "consumed", "evidence" };
            if (version is not ("1" or "2") || extra.Any(n => root.TryGetProperty(n, out _) != (version == "2")))
            { throw new InvalidDataException(); }
            var value = JsonSerializer.Deserialize<Transition>(bytes, TransitionJson) ?? throw new InvalidDataException();
            var heads = value.Heads.Select(h => Codec.FromBase64Url(h ?? throw new InvalidDataException(), 32, "head")).ToArray();
            if (value.AcceptHostSign || !Codec.IsId(value.Generation) || heads.Length is < 1 or > 4 ||
                heads.Zip(heads.Skip(1), (a, b) => Ids.HeadOrder.Compare(a, b) >= 0).Any(b => b) ||
                value.Requests.Length > ReplayLimit || value.Requests.Select(r => r?.Request).Distinct().Count() != value.Requests.Length)
            {
                throw new InvalidDataException();
            }
            Codec.FromBase64Url(value.Genesis, 32, "genesis");
            Instant.Parse(value.HighWater);
            if (value.Floor is { } floor) { Instant.Parse(floor); }
            var highest = value.HighestIssuedAt is { } at ? Instant.Parse(at) : (Instant?)null;
            foreach (var replay in value.Requests)
            {
                if (replay is null || !Guid.TryParseExact(replay.Request, "D", out var request) || replay.Request != request.ToString("D"))
                {
                    throw new InvalidDataException();
                }
                var issued = Instant.Parse(replay.IssuedAt);
                var expires = Instant.Parse(replay.ExpiresAt);
                if (expires <= issued || expires > issued.AddSeconds(PersonCommand.MaxLifetimeSeconds) || highest is null || issued > highest.Value)
                {
                    throw new InvalidDataException();
                }
            }
            if (version == "2" && value.Consumed is null) { throw new InvalidDataException(); }
            var consumed = value.Consumed ?? [];
            var capHighest = value.CapHighestIssuedAt is { } capAt ? Instant.Parse(capAt) : (Instant?)null;
            if (consumed.Length > 32 || consumed.Select(r => r?.Request).Distinct().Count() != consumed.Length ||
                consumed.Select(r => r?.Nonce).Distinct().Count() != consumed.Length) { throw new InvalidDataException(); }
            foreach (var record in consumed)
            {
                ValidateConsumed(record);
                if (capHighest is null || Instant.Parse(record.IssuedAt) > capHighest.Value) { throw new InvalidDataException(); }
            }
            if (value.Evidence is { } evidence) { ValidateEvidence(evidence); }
            return value with { V = 2, Consumed = consumed };
        });
        if (issue is not null) { return issue; }
        if (next is null) { return transition is null ? null : "pin-unreadable"; }
        if (pin is null || next.Account != pin.Account || next.Generation != pin.Generation || next.Genesis != Codec.Base64Url(pin.Genesis))
        {
            return "pin-mismatch";
        }
        var frontier = next.Heads.Select(h => Codec.FromBase64Url(h, 32, "head")).ToArray();
        if (frontier.Any(h => !Descends(verified, h)) ||
            pin.Heads.Concat(TransitionHeads).Any(h => !Descends(verified, h, frontier))) { return "head-regress"; }
        if (transition is { } prior && (Regressed(prior.HighWater, next.HighWater) || Regressed(prior.Floor, next.Floor) ||
            Regressed(prior.HighestIssuedAt, next.HighestIssuedAt) || Regressed(prior.CapHighestIssuedAt, next.CapHighestIssuedAt) ||
            prior.Consumed!.Any(r => Instant.Parse(r.ExpiresAt).AddSeconds(120) >= Instant.Parse(next.HighWater) && !next.Consumed!.Contains(r)) ||
            prior.Requests.Any(r =>
                Instant.Parse(r.ExpiresAt).AddSeconds(120) >= Instant.Parse(next.HighWater) && !next.Requests.Contains(r))))
        { return "pin-unreadable"; }
        transition = next;
        return null;
    }

    string? CheckTransition(PersonCommand command, CommandContext context, out CommandContext effective)
    {
        effective = context;
        if (problem is not null) { return problem; }
        if (pin is null) { return "unpinned"; }
        if (Verified is not { } verified) { return "no-roster"; }
        if (ReloadTransition(verified) is { } issue) { return issue; }
        var now = transition is null ? context.Now : Later(context.Now, Instant.Parse(transition.HighWater));
        var floor = transition?.Floor is { } at ? Instant.Parse(at) : context.Floor;
        if (context.Floor is { } supplied) { floor = floor is { } held ? Later(held, supplied) : supplied; }
        effective = context with { Action = CommandAction.AcceptHostSign, Now = now, Floor = floor, Previous = verified.Heads };
        return PersonCommand.Check(command, verified, effective);
    }

    public string? CheckAcceptHostSign(PersonCommand command, CommandContext context)
    {
        lock (sync) { return CheckTransition(command, context, out _) ?? "unsupported-verifier"; }
    }

    // Foundation only: records replay/clock state, never authorization or command success.
    internal string? RecordCommand(PersonCommand command, CommandContext context)
    {
        lock (sync)
        {
            if (CheckTransition(command, context, out _) is { } refusal) { return refusal; }
            if (pin is not { } identity || Verified is not { } verified) { return "unpinned"; }
            try
            {
                var lockPath = TransitionPath + ".lock";
                if (ReadPrivate(lockPath, 0, bytes => bytes).Problem is not null) { return "transition-store"; }
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                using var lease = new FileStream(lockPath, options);
                var (diskPin, pinIssue) = ReadPin(pinPath);
                if (pinIssue is not null) { return pinIssue; }
                if (diskPin is null || diskPin.Account != identity.Account || diskPin.Generation != identity.Generation ||
                    !diskPin.Genesis.AsSpan().SequenceEqual(identity.Genesis)) { return "pin-mismatch"; }
                if (diskPin.Heads.Any(h => !Descends(verified, h))) { return "head-regress"; }
                if (CheckTransition(command, context, out var effective) is { } changed) { return changed; }
                var records = transition?.Requests.Where(r => Instant.Parse(r.ExpiresAt).AddSeconds(120) >= effective.Now).ToList() ?? [];
                var body = command.Body;
                var request = Guid.ParseExact(body.Request, "D").ToString("D");
                if (records.Any(r => r.Request == request)) { return "replay"; }
                if (records.Count >= ReplayLimit) { return "replay-full"; }
                records.Add(new Replay(request, body.IssuedAt, body.ExpiresAt));
                var highest = transition?.HighestIssuedAt is { } previous && Instant.Parse(previous) > Instant.Parse(body.IssuedAt)
                    ? previous : body.IssuedAt;
                var next = new Transition(2, identity.Account, identity.Generation, Codec.Base64Url(identity.Genesis),
                    [.. verified.Heads.Select(h => Codec.Base64Url(h))], false, Time(effective.Now),
                    effective.Floor is { } floor ? Time(floor) : null, highest, [.. records],
                    transition?.CapHighestIssuedAt, transition?.Consumed ?? [], transition?.Evidence);
                return CommitTransition(next);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                return "transition-store";
            }
        }
    }

    static void ValidateConsumed(CapConsumed? record)
    {
        if (record is null || !Guid.TryParseExact(record.Request, "D", out var request) || record.Request != request.ToString("D"))
        { throw new InvalidDataException(); }
        Codec.FromBase64Url(record.Nonce, 32, "nonce"); Codec.FromBase64Url(record.Consent, 32, "consent");
        var issued = Instant.Parse(record.IssuedAt); var expires = Instant.Parse(record.ExpiresAt);
        if (expires <= issued || expires > issued.AddSeconds(600)) { throw new InvalidDataException(); }
    }

    static void ValidateEvidence(CapEvidence evidence)
    {
        if (evidence.Collection.Length > 176128 ||
            Codec.FromBase64Url(evidence.Collection, "collection").Length > VerifierSupport.MaxCollectionBytes)
        { throw new InvalidDataException(); }
        Codec.FromBase64Url(evidence.Frontier, 32, "frontier"); Codec.FromBase64Url(evidence.Verifiers, 32, "verifiers");
        Instant.Parse(evidence.ExpiresAt);
    }

    internal CapEvidence? ReadEvidence()
    {
        lock (sync)
        {
            if (problem is not null) { throw new InvalidDataException(problem); }
            if (Verified is not { } verified) { throw new InvalidOperationException(pin is null ? "unpinned" : "no-roster"); }
            if (ReloadTransition(verified) is { } issue) { throw new InvalidDataException(issue); }
            return transition?.Evidence;
        }
    }

    internal string? ConsumeCapability(CapConsumed record, Instant now) =>
        WriteCapability(now, prior =>
        {
            ValidateConsumed(record);
            var consumed = prior.Consumed ?? throw new InvalidDataException();
            if (consumed.Any(r => r.Request == record.Request || r.Nonce == record.Nonce)) { return (null, "replay"); }
            var high = Later(now, Instant.Parse(prior.HighWater));
            var resetClock = now < Instant.Parse(prior.HighWater);
            var records = consumed.Where(r => resetClock || Instant.Parse(r.ExpiresAt).AddSeconds(120) >= high).ToList();
            if (records.Count == 32) { return (null, "consumed-full"); }
            var highest = prior.CapHighestIssuedAt is { } at && Instant.Parse(at) > Instant.Parse(record.IssuedAt) ? at : record.IssuedAt;
            var floor = prior.Floor;
            if (resetClock)
            {
                var reset = Later(now, Instant.Parse(highest));
                floor = Time(floor is { } held ? Later(Instant.Parse(held), reset) : reset);
            }
            records.Add(record);
            return (prior with { HighWater = Time(high), Floor = floor, Consumed = [.. records], CapHighestIssuedAt = highest }, null);
        });

    internal string? StoreEvidence(CapEvidence evidence) => WriteCapability(null, prior =>
    {
        ValidateEvidence(evidence);
        return (prior with { Evidence = evidence }, null);
    });

    string? WriteCapability(Instant? now, Func<Transition, (Transition? Next, string? Issue)> change)
    {
        lock (sync)
        {
            if (problem is not null) { return problem; }
            if (pin is not { } identity) { return "unpinned"; }
            if (Verified is not { } verified) { return "no-roster"; }
            try
            {
                var lockPath = TransitionPath + ".lock";
                if (ReadPrivate(lockPath, 0, bytes => bytes).Problem is not null) { return "transition-store"; }
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                using var lease = new FileStream(lockPath, options);
                var (diskPin, pinIssue) = ReadPin(pinPath);
                if (pinIssue is not null) { return pinIssue; }
                if (diskPin is null || diskPin.Account != identity.Account || diskPin.Generation != identity.Generation ||
                    !diskPin.Genesis.AsSpan().SequenceEqual(identity.Genesis)) { return "pin-mismatch"; }
                if (diskPin.Heads.Any(h => !Descends(verified, h))) { return "head-regress"; }
                if (ReloadTransition(verified) is { } issue) { return issue; }
                if (transition is null && now is null) { return "transition-store"; }
                var prior = transition ?? new Transition(2, identity.Account, identity.Generation, Codec.Base64Url(identity.Genesis),
                    [.. verified.Heads.Select(h => Codec.Base64Url(h))], false, Time(now!.Value), null, null, [], null, [], null);
                var (next, refusal) = change(prior);
                if (refusal is not null) { return refusal; }
                var updated = next ?? throw new InvalidDataException();
                return CommitTransition(updated with { Heads = [.. verified.Heads.Select(h => Codec.Base64Url(h))] });
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                FormatException or JsonException or RosterFormatException or System.Security.SecurityException)
            { return "transition-store"; }
        }
    }

    string? CommitTransition(Transition next)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, TransitionJson);
        if (bytes.Length > TransitionLimit) { return "transition-store"; }
        var temporary = TransitionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = PrivateFolder.CreateNew(temporary)) { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            BeforeTransitionCommit?.Invoke();
            File.Move(temporary, TransitionPath, overwrite: true);
        }
        finally { File.Delete(temporary); }
        transition = next;
        return null;
    }

    static Instant Later(Instant a, Instant b) => a > b ? a : b;
    static bool Regressed(string? before, string? after) => before is not null && (after is null || Instant.Parse(after) < Instant.Parse(before));
    static string Time(Instant instant) =>
        DateTimeOffset.FromUnixTimeSeconds(instant.Seconds).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + $".{instant.Nanos:D9}Z";
    internal Action? BeforeTransitionCommit { get; set; }
}
