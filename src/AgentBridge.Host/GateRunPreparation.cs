using System.Collections.Concurrent;
using System.Text.Json;
using AgentBridge.Transport;
using AgentBridge.Host.Core;

namespace AgentBridge.Host;

public sealed record GateRunParticipant(string Host, string Profile, string Instrumentation, string AgentsHead, string LaunchesHead);
public sealed record GateRunEnrollment(IReadOnlyList<GateRunParticipant> Participants, Guid Recorder, long Cursor, string Digest)
{
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    internal bool Valid() => Participants is { Count: 4 } && Recorder != Guid.Empty && Cursor >= 0 && Hash(Digest)
        && Participants.All(p => p is not null && p.Host is { Length: > 0 and <= 128 }
            && p.Host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            && Identity.IsValidProfile(p.Profile) && p.Instrumentation is { Length: > 0 and <= 64 }
            && Version.TryParse(p.Instrumentation, out _) && Hash(p.AgentsHead) && Hash(p.LaunchesHead))
        && Participants.Select(p => p.Host).Distinct(StringComparer.Ordinal).Count() == 4;
}

public interface IGateRunEnrollmentProvider
{
    GateRunEnrollment? Read();
    event Action<GateRunEnrollment?>? Changed;
}

public sealed class HeldGateRunEnrollmentProvider : IGateRunEnrollmentProvider
{
    public GateRunEnrollment? Read() => null;
    public event Action<GateRunEnrollment?>? Changed { add { } remove { } }
}

internal static class GateRunPreparation
{
    internal const string PersonRequired = "gate controls require a host-pinned person through the authenticated relay";
    static readonly ConcurrentDictionary<string, RunState> States = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    static string Key(string profile) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
    static RunState State(AgentAuditLog audit) => States.GetOrAdd(Key(audit.Profile), _ => new());
    internal static Lock Sync(AgentAuditLog audit) => State(audit).Sync;
    internal static bool Held(string profile) => States.TryGetValue(Key(profile), out var state) && (state.Unavailable || !state.Pending.IsEmpty);
    internal static bool IsControl(string op) => op is "gate.start" or "gate.pause" or "gate.resume" or "gate.reset" or "gate.seal";
    internal static bool IsBegin(AuditCommands.Row row) => row.Gate?.Phase == "control"
        && (row.Action == "gate.start" && row.Outcome == "prepared" || row.Action == "gate.reset" && row.Outcome == "reset");
    internal static bool Voided(IReadOnlyList<AuditCommands.Row> rows, Guid run) => rows.Any(r => r.Gate is { Phase: "control" } g
        && (g.Run == run && r.Outcome == "invalidated" || r.Action == "gate.reset" && r.Outcome == "reset" && g.PreviousRun == run));

    internal static Guid Request(JsonElement data, string op)
    {
        if (data.ValueKind != JsonValueKind.Object || data.EnumerateObject().Count() != 1
            || !data.TryGetProperty("request", out var value) || value.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(value.GetString(), "D", out var request) || request == Guid.Empty)
        { throw new ArgumentException($"{op} accepts only a request UUID"); }
        return request;
    }

    internal static List<AuditCommands.Row> Read(AgentAuditLog audit)
    {
        if (!State(audit).Reconcile(audit)) { throw new InvalidOperationException("gate control held: trusted enrollment unavailable or roster observation pending"); }
        return ReadEvidence(audit);
    }

    static List<AuditCommands.Row> ReadEvidence(AgentAuditLog audit)
    {
        long retained = 0;
        var (rows, complete) = GateAuditProjection.Load(audit.Profile, boundary: (floor, _) => retained = floor);
        if (!complete || retained > 0) { throw new InvalidOperationException("gate control refused: audit unavailable"); }
        return rows;
    }

    internal static bool HasPreparedRun(List<AuditCommands.Row> rows) =>
        rows.Any(r => IsBegin(r) && !Voided(rows, r.Gate!.Run));

    internal static object Prepare(AgentAuditLog audit, string principal, JsonElement data, IGateRunEnrollmentProvider provider)
    {
        var request = Request(data, "gate.start");
        lock (Sync(audit))
        {
            var rows = Read(audit);
            var matching = rows.Where(r => r.Action == "gate.start" && r.Request == request.ToString()).ToArray();
            if (matching.Length > 0)
            {
                if (matching.Length != 1 || matching[0] is not { Outcome: "prepared", Principal: var owner,
                    Gate: { Enrollment: not null } previous } || owner != principal)
                { throw new InvalidOperationException("gate control refused: conflicting request binding"); }
                return Reply(previous.Run);
            }
            if (HasPreparedRun(rows))
            { throw new InvalidOperationException("gate control refused: a run is already prepared"); }
            var enrollment = provider.Read();
            if (enrollment?.Valid() != true) { throw new InvalidOperationException("gate control held: trusted enrollment unavailable"); }
            enrollment = enrollment with { Participants = enrollment.Participants.ToArray() };
            var run = Guid.NewGuid();
            var metadata = new GateAuditMetadata(1, Guid.NewGuid(), run, 0, "control", "relay", "person-client",
                Guid.Empty, "none", Enrollment: enrollment);
            if (!audit.Append(new("in", request.ToString(), principal, [], "gate.start", "profile", "prepared", Gate: metadata)))
            { throw new InvalidOperationException("gate control refused: audit unavailable"); }
            return Reply(run);
        }
    }

    internal static object Reset(AgentAuditLog audit, string principal, JsonElement data, IGateRunEnrollmentProvider provider)
    {
        var request = Request(data, "gate.reset");
        lock (Sync(audit))
        {
            var rows = Read(audit);
            if (ResetReplay(rows, request, principal) is { } replay) { return replay; }
            var previous = rows.Where(IsBegin).MaxBy(r => r.Gate!.Sequence)
                ?? throw new InvalidOperationException("gate reset refused: no run to reset");
            var enrollment = provider.Read();
            if (enrollment?.Valid() != true) { throw new InvalidOperationException("gate control held: trusted enrollment unavailable"); }
            if (ResetReplay(Read(audit), request, principal) is { } winner) { return winner; }
            var run = Guid.NewGuid();
            var metadata = new GateAuditMetadata(1, Guid.NewGuid(), run, 0, "control", "relay", "person-client", Guid.Empty, "none",
                Enrollment: enrollment with { Participants = enrollment.Participants.ToArray() }, PreviousRun: previous.Gate!.Run);
            if (!audit.Append(new("in", request.ToString(), principal, [], "gate.reset", "profile", "reset", Gate: metadata)))
            { throw new InvalidOperationException("gate reset refused: audit unavailable"); }
            audit.ControlWriterRun = null;
            audit.RunPause.Dispose();
            return Reply(run);
        }
    }

    static object? ResetReplay(List<AuditCommands.Row> rows, Guid request, string principal)
    {
        var matches = rows.Where(r => r.Action == "gate.reset" && r.Outcome == "reset" && r.Request == request.ToString()).ToArray();
        if (matches.Length == 0) { return null; }
        if (matches.Length != 1 || matches[0].Principal != principal)
        { throw new InvalidOperationException("gate reset refused: conflicting request binding"); }
        return Reply(matches[0].Gate!.Run);
    }

    internal static IDisposable Observe(AgentAuditLog audit, SessionSource source, IGateRunEnrollmentProvider provider)
    {
        var observer = new EnrollmentObserver(audit, source, provider);
        observer.Check();
        return observer;
    }

    sealed class RunState
    {
        internal readonly Lock Sync = new();
        internal readonly ConcurrentQueue<GateRunEnrollment?> Pending = new();
        internal volatile bool Unavailable;
        internal bool Reconcile(AgentAuditLog audit)
        {
            lock (Sync)
            {
                try
                {
                    while (Pending.TryPeek(out var current))
                    {
                        if (current is null)
                        { Unavailable = true; audit.ControlUnavailable(); Pending.TryDequeue(out _); continue; }
                        var rows = ReadEvidence(audit);
                        foreach (var begin in rows.Where(r => IsBegin(r) && !Voided(rows, r.Gate!.Run)
                            && !Same(r.Gate!.Enrollment!, current)))
                        {
                            audit.ControlWriterRun = null;
                            if (!audit.Append(new("in", Guid.NewGuid().ToString(), begin.Principal, [], begin.Action, "profile", "invalidated",
                                Gate: begin.Gate! with { Operation = Guid.NewGuid(), Enrollment = null, PreviousRun = null, ReasonCode = "roster-change" })))
                            { throw new IOException("gate roster void remains pending"); }
                            audit.RunPause.Dispose();
                        }
                        Pending.TryDequeue(out _);
                        Unavailable = false;
                    }
                    return !Unavailable && Pending.IsEmpty;
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or JsonException)
                { Unavailable = true; audit.ControlUnavailable(); return false; }
            }
        }
        static bool Same(GateRunEnrollment left, GateRunEnrollment right) => left.Recorder == right.Recorder
            && Identities(left).SequenceEqual(Identities(right));
        static (string Host, string Profile, string Instrumentation)[] Identities(GateRunEnrollment value) =>
            value.Participants.Select(p => (p.Host, p.Profile, p.Instrumentation)).OrderBy(p => p.Host, StringComparer.Ordinal).ToArray();
    }

    sealed class EnrollmentObserver : IDisposable
    {
        readonly AgentAuditLog audit;
        readonly SessionSource source;
        readonly IGateRunEnrollmentProvider provider;
        internal EnrollmentObserver(AgentAuditLog audit, SessionSource source, IGateRunEnrollmentProvider provider)
        {
            this.audit = audit;
            this.source = source;
            this.provider = provider;
            source.GateRosterChanged += Check;
            provider.Changed += Changed;
        }
        void Capture(GateRunEnrollment? value) => State(audit).Pending.Enqueue(value?.Valid() == true
            ? value with { Participants = value.Participants.ToArray() } : null);
        void Changed(GateRunEnrollment? value) { Capture(value); source.NotifyGateRosterChanged(); }
        internal void Check()
        {
            try { Capture(provider.Read()); }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
            { Capture(null); }
            State(audit).Reconcile(audit);
        }
        public void Dispose()
        {
            provider.Changed -= Changed;
            source.GateRosterChanged -= Check;
        }
    }

    internal static object Reply(Guid run) => new { outcome = "prepared", run, held = true, reason = "all-host-barrier-unavailable" };
}
