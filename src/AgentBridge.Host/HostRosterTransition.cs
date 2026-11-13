using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Roster;

namespace AgentBridge.Host;

public sealed partial class HostRosterReader
{
    sealed record Replay(string Request, string IssuedAt, string ExpiresAt);
    sealed record Transition(int V, string Account, string Generation, string Genesis, string[] Heads, bool AcceptHostSign,
        string HighWater, string? Floor, string? HighestIssuedAt, Replay[] Requests);
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
            var value = JsonSerializer.Deserialize<Transition>(bytes, TransitionJson) ?? throw new InvalidDataException();
            var heads = value.Heads.Select(h => Codec.FromBase64Url(h ?? throw new InvalidDataException(), 32, "head")).ToArray();
            if (value.V != 1 || value.AcceptHostSign || !Codec.IsId(value.Generation) || heads.Length is < 1 or > 4 ||
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
                if (replay is null || !Guid.TryParseExact(replay.Request, "D", out _) || replay.Request != replay.Request.ToLowerInvariant())
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
            return value;
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
            Regressed(prior.HighestIssuedAt, next.HighestIssuedAt) || prior.Requests.Any(r =>
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
                if (records.Any(r => r.Request == body.Request)) { return "replay"; }
                if (records.Count >= ReplayLimit) { return "replay-full"; }
                records.Add(new Replay(body.Request, body.IssuedAt, body.ExpiresAt));
                var highest = transition?.HighestIssuedAt is { } previous && Instant.Parse(previous) > Instant.Parse(body.IssuedAt)
                    ? previous : body.IssuedAt;
                var next = new Transition(1, identity.Account, identity.Generation, Codec.Base64Url(identity.Genesis),
                    [.. verified.Heads.Select(h => Codec.Base64Url(h))], false, Time(effective.Now),
                    effective.Floor is { } floor ? Time(floor) : null, highest, [.. records]);
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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                return "transition-store";
            }
        }
    }

    static Instant Later(Instant a, Instant b) => a > b ? a : b;
    static bool Regressed(string? before, string? after) => before is not null && (after is null || Instant.Parse(after) < Instant.Parse(before));
    static string Time(Instant instant) =>
        DateTimeOffset.FromUnixTimeSeconds(instant.Seconds).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + $".{instant.Nanos:D9}Z";
    internal Action? BeforeTransitionCommit { get; set; }
}
