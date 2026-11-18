using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentBridge.Transport;

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
}

public sealed class HeldGateRunEnrollmentProvider : IGateRunEnrollmentProvider
{
    public GateRunEnrollment? Read() => null;
}

internal static class GateRunPreparation
{
    internal const string PersonRequired = "gate controls require a host-pinned person through the authenticated relay";
    static readonly ConditionalWeakTable<AgentAuditLog, Lock> Locks = new();
    internal static Lock Sync(AgentAuditLog audit) => Locks.GetValue(audit, _ => new());
    internal static bool IsControl(string op) => op is "gate.start" or "gate.pause" or "gate.resume" or "gate.reset" or "gate.seal";

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
        long retained = 0;
        var (rows, complete) = GateAuditProjection.Load(audit.Profile, boundary: (floor, _) => retained = floor);
        if (!complete || retained > 0) { throw new InvalidOperationException("gate control refused: audit unavailable"); }
        return rows;
    }

    internal static bool HasPreparedRun(List<AuditCommands.Row> rows) =>
        rows.Any(r => r.Action == "gate.start" && r.Outcome == "prepared"
            && !rows.Any(v => v.Gate?.Run == r.Gate?.Run && v.Outcome == "invalidated"));

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

    internal static object Reply(Guid run) => new { outcome = "prepared", run, held = true, reason = "all-host-barrier-unavailable" };
}
